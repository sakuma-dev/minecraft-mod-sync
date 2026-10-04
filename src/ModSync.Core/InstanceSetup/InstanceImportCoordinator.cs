namespace ModSync.Core.InstanceSetup;

public sealed record ImportWaitOptions(TimeSpan Interval, TimeSpan Confirmation, TimeSpan Staging, TimeSpan Unstable)
{
    public static ImportWaitOptions Default { get; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2));
}

/// <summary>画面から独立して、観測した配置と記録だけで登録可否を決める。</summary>
public sealed class InstanceImportCoordinator(IPrismImportGateway gateway, IImportRecordStore store,
    TimeProvider? timeProvider = null, ImportWaitOptions? waitOptions = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ImportWaitOptions limits = waitOptions ?? ImportWaitOptions.Default;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Key => gateway.Installation.DataRootKey;

    public async Task<ImportAttemptStatus> StartAsync(ImportInput input, IProgress<ImportAttemptStatus>? progress = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        var record = new ImportOperationRecord { OperationId = Guid.NewGuid(), PackId = input.Definition.PackId,
            InputSetId = input.Definition.InputSetId, Purpose = input.Definition.Purpose, Prism = gateway.Installation };
        try
        {
            var held = await store.TryAcquireLockAsync(Key, record.OperationId, false, ct);
            await using var handle = held.Handle;
            if (handle == null || held.Stale) return new(record.OperationId, ImportAttemptState.PrerequisiteFailed, "anotherOperationInProgress", true);
            var previous = await store.LoadOperationsAsync(Key, ct);
            if (previous.Any(r => !r.Terminal || r.Recheck.LastOrDefault()?.Outcome != "noCandidate"))
                return new(record.OperationId, ImportAttemptState.PrerequisiteFailed, "recheckRequired: " +
                    string.Join(", ", previous.Where(r => !r.Terminal || r.Recheck.LastOrDefault()?.Outcome != "noCandidate").Select(r => r.OperationId)), true);
            if (await store.LoadRegistrationAsync(Key, record.PackId, ct) != null)
                return new(record.OperationId, ImportAttemptState.PrerequisiteFailed, "alreadyRegisteredToAnotherInstance", true);
            // operationIdを永続化してから生成する。途中終了時は再確認が必要になる。
            record.State = ImportAttemptState.Prepared;
            record.UpdatedAt = clock.GetUtcNow();
            await store.SaveOperationAsync(record, ct);
            var built = await VerificationPackBuilder.BuildAsync(input, record.OperationId, ct);
            if (built.Plan == null || built.Archive == null)
                return await Set(record, ImportAttemptState.PrerequisiteFailed, string.Join(" / ", built.Errors), progress);
            record.Input = built.Plan;
            var archive = await store.SaveArchiveAsync(Key, built.Plan, built.Archive, ct);
            record.Before = await gateway.CaptureSnapshotAsync(ct);
            if (!record.Before.Entries.Any(e => e.Kind == "directory" && e.HasInstanceCfg))
                return await Set(record, ImportAttemptState.PrerequisiteFailed, "instancesRootInvalid", progress);
            await Set(record, ImportAttemptState.Prepared, "prepared", progress);
            record.Request = await gateway.RequestImportAsync(archive, ct);
            await Set(record, ImportAttemptState.ImportRequested, "requested", progress);
            if (record.Request.Delivery == "runningInstance" && record.Request.ProcessExitedAt is { } exited &&
                exited - record.Request.RequestedAt < TimeSpan.FromSeconds(5))
                await Set(record, ImportAttemptState.HandedOffToRunningPrism, "handedOff", progress);
            return await ObserveUntilStable(record, progress, ct);
        }
        catch (OperationCanceledException) { return await Failure(record, ImportAttemptState.CancelledByApp, "userCancelledWait", progress); }
        catch (Exception error) { return await Failure(record, ImportAttemptState.Unobservable, "ioError: " + error.Message, progress); }
        finally { gate.Release(); }
    }

    private async Task<ImportAttemptStatus> ObserveUntilStable(ImportOperationRecord record, IProgress<ImportAttemptStatus>? progress, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var changedAt = started;
        DateTimeOffset? candidateSince = null;
        string[] lastStaging = [];
        var sawStaging = false;
        CandidateFacts? last = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await gateway.CaptureSnapshotAsync(ct);
            var staging = snapshot.StagingKeys.Except(record.Before!.StagingKeys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var decision = CandidateEvaluator.Evaluate(record.Input!, record.Before, await gateway.ObserveCandidatesAsync(record.Before, ct));
            record.UnrelatedNewEntries = decision.Unrelated;
            if (ImportAttemptStateMachine.IsTerminal(decision.State)) return await Set(record, decision.State, decision.Reason, progress);
            var now = clock.GetUtcNow();
            if (staging.Length > 0)
            {
                sawStaging = true;
                if (!lastStaging.SequenceEqual(staging)) changedAt = now;
            }
            if (decision.Candidate != null)
            {
                var same = CandidateEvaluator.SameFingerprint(last, decision.Candidate);
                if (same && staging.Length == 0)
                {
                    record.Candidate = decision.Candidate;
                    record.Readiness = ReadinessEvaluator.Evaluate(await gateway.ProbeReadinessAsync(decision.Candidate.InstancePath, record.Input!.Definition.Components, ct), now);
                    return await Set(record, ImportAttemptState.Verified, "stable", progress);
                }
                if (!same) { candidateSince ??= now; changedAt = now; }
                else candidateSince = null;
                last = decision.Candidate;
            }
            else { last = null; candidateSince = null; }
            if (sawStaging && staging.Length == 0 && decision.Candidate == null)
                return await Set(record, ImportAttemptState.StagingVanished, "stagingVanished", progress);
            if (candidateSince != null && now - candidateSince >= limits.Unstable)
                return await Set(record, ImportAttemptState.TimedOut, "unstable", progress);
            if (staging.Length > 0 && now - changedAt >= limits.Staging)
                return await Set(record, ImportAttemptState.TimedOut, "stagingStalled", progress);
            if (staging.Length == 0 && decision.Candidate == null && now - started >= limits.Confirmation)
                return await Set(record, ImportAttemptState.TimedOut, "noActivity", progress);
            await Set(record, staging.Length > 0 || decision.Candidate != null ? ImportAttemptState.Writing : ImportAttemptState.AwaitingUserConfirmation,
                staging.Length > 0 ? "stagingAppeared" : decision.Candidate != null ? "candidateChanging" : "noActivity", progress);
            lastStaging = staging;
            await Task.Delay(limits.Interval, clock, ct);
        }
    }

    public async Task<ImportAttemptStatus> RegisterAsync(Guid operationId, CancellationToken ct = default) =>
        await WithRecord(operationId, async record =>
        {
            var registered = await store.LoadRegistrationAsync(Key, record.PackId, ct);
            if (record.State is ImportAttemptState.Registered or ImportAttemptState.AlreadyRegistered)
                return await Set(record, RegistrationMatches(record, registered) ? ImportAttemptState.AlreadyRegistered : ImportAttemptState.CommitFailed,
                    RegistrationMatches(record, registered) ? "alreadyRegistered" : "conflictingRegistration");
            if (record.State != ImportAttemptState.Verified || record.Candidate == null || record.Input == null || record.Before == null)
                return Status(record, "notVerified");
            var snapshot = await gateway.CaptureSnapshotAsync(ct);
            var decision = CandidateEvaluator.Evaluate(record.Input, record.Before, await gateway.ObserveCandidatesAsync(record.Before, ct));
            if (decision.State != ImportAttemptState.Verified) return await Set(record, decision.State, decision.Reason);
            if (snapshot.StagingKeys.Except(record.Before.StagingKeys, StringComparer.OrdinalIgnoreCase).Any() ||
                !CandidateEvaluator.SameFingerprint(record.Candidate, decision.Candidate))
                return await Set(record, ImportAttemptState.Writing, "candidateChanging");
            record.Candidate = decision.Candidate;
            record.Readiness = ReadinessEvaluator.Evaluate(await gateway.ProbeReadinessAsync(record.Candidate!.InstancePath, record.Input.Definition.Components, ct), clock.GetUtcNow());
            try
            {
                await Set(record, ImportAttemptState.Committing, "committing");
                var c = record.Candidate;
                var registration = new RegistrationRecord(1, "modsync.prism-instance-registration", record.PackId, operationId, record.Purpose,
                    record.Prism.DataRoot, Key, c.InstanceId, c.InstancePath, c.GameRoot, c.DisplayName, record.Input, c.Components,
                    c.Fingerprint, record.Readiness.Level, clock.GetUtcNow());
                var commit = await store.CommitRegistrationAsync(registration, ct);
                if (!commit.Success) return await Set(record, ImportAttemptState.CommitFailed, commit.Reason);
                // この保存が失敗してもCommittingを上書きしない。次回再確認で回復する。
                return await Set(record, commit.AlreadyRegistered ? ImportAttemptState.AlreadyRegistered : ImportAttemptState.Registered, "readBackMatched");
            }
            catch (Exception error)
            {
                record.State = ImportAttemptState.Committing;
                record.Terminal = false;
                return new(operationId, ImportAttemptState.CommitFailed, "writeFailed: " + error.Message, true);
            }
        }, ct);

    public async Task<ImportAttemptStatus> RecheckAsync(Guid operationId, CancellationToken ct = default) =>
        await WithRecord(operationId, async record =>
        {
            if (record.Input == null || record.Before == null)
            {
                record.Recheck.Add(new(clock.GetUtcNow(), "noCandidate"));
                return await Set(record, ImportAttemptState.PrerequisiteFailed, "inputNotPrepared");
            }
            var registration = await store.LoadRegistrationAsync(Key, record.PackId, ct);
            if (RegistrationMatches(record, registration))
            {
                record.RecoveredAt = clock.GetUtcNow();
                record.Recheck.Add(new(clock.GetUtcNow(), "alreadyRegistered"));
                return await Set(record, ImportAttemptState.Registered, "recoveredRegistration");
            }
            if (record.State == ImportAttemptState.Committing)
                return await Set(record, ImportAttemptState.CommitFailed, "conflictingRegistration");
            var decision = CandidateEvaluator.Evaluate(record.Input, record.Before, await gateway.ObserveCandidatesAsync(record.Before, ct));
            var outcome = decision.Candidate != null ? "lateCandidateFound" : decision.State == ImportAttemptState.Ambiguous ? "ambiguous" :
                decision.State == ImportAttemptState.Unobservable ? "unobservable" : decision.State == ImportAttemptState.Mismatch ? "mismatch" : "noCandidate";
            record.Recheck.Add(new(clock.GetUtcNow(), outcome));
            if (decision.Candidate != null) return await ObserveUntilStable(record, null, ct);
            return await Set(record, decision.State == ImportAttemptState.AwaitingUserConfirmation ? ImportAttemptState.TimedOut : decision.State, outcome);
        }, ct);

    public async Task<ImportAttemptStatus> CheckReadinessAsync(Guid operationId, CancellationToken ct = default) =>
        await WithRecord(operationId, async record =>
        {
            if (record.Candidate == null || record.Input == null) return Status(record, "candidateMissing");
            record.Readiness = ReadinessEvaluator.Evaluate(await gateway.ProbeReadinessAsync(record.Candidate.InstancePath, record.Input.Definition.Components, ct), clock.GetUtcNow());
            await store.SaveOperationAsync(record, ct);
            return Status(record, "readiness: " + record.Readiness.Level);
        }, ct);

    public async Task ReportUserActionAsync(Guid operationId, string report, CancellationToken ct = default)
    {
        if (report is not "cancelledInPrism" and not "errorShownInPrism" and not "confirmedInPrism" and not "renamedInDialog")
            throw new ArgumentException("未対応の申告です。", nameof(report));
        await WithRecord(operationId, async record =>
        {
            record.UserReports.Add(new(clock.GetUtcNow(), report));
            await store.SaveOperationAsync(record, ct);
            return Status(record, "userReportRecorded");
        }, ct);
    }

    private async Task<ImportAttemptStatus> WithRecord(Guid id, Func<ImportOperationRecord, Task<ImportAttemptStatus>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        ImportOperationRecord? record = null;
        try
        {
            var held = await store.TryAcquireLockAsync(Key, id, true, ct);
            await using var handle = held.Handle;
            if (handle == null) return new(id, ImportAttemptState.PrerequisiteFailed, "anotherOperationInProgress", true);
            record = await store.LoadOperationAsync(Key, id, ct);
            if (record == null) return new(id, ImportAttemptState.Unobservable, "recordMissing", true);
            if (!StringComparer.OrdinalIgnoreCase.Equals(record.Prism.DataRoot, gateway.Installation.DataRoot))
                return new(id, ImportAttemptState.PrerequisiteFailed, "dataRootMismatch", true);
            return await action(record);
        }
        catch (OperationCanceledException) { return record == null ? new(id, ImportAttemptState.CancelledByApp, "userCancelledWait", true) : await Failure(record, ImportAttemptState.CancelledByApp, "userCancelledWait"); }
        catch (Exception error) { return record == null ? new(id, ImportAttemptState.Unobservable, "ioError: " + error.Message, true) : await Failure(record, ImportAttemptState.Unobservable, "ioError: " + error.Message); }
        finally { gate.Release(); }
    }
    private static bool RegistrationMatches(ImportOperationRecord r, RegistrationRecord? registration) => registration != null && r.Input != null && r.Candidate != null &&
        registration.PackId == r.PackId && registration.OperationId == r.OperationId && registration.Input.ArchiveSha256 == r.Input.ArchiveSha256 &&
        registration.Input.IdentitySha256 == r.Input.IdentitySha256 && registration.Input.IndexSha256 == r.Input.IndexSha256 &&
        registration.Input.ProbeSha256 == r.Input.ProbeSha256 && registration.Input.TemplateSha256 == r.Input.TemplateSha256 &&
        StringComparer.OrdinalIgnoreCase.Equals(registration.DataRoot, r.Prism.DataRoot) &&
        StringComparer.OrdinalIgnoreCase.Equals(registration.InstanceId, r.Candidate.InstanceId) &&
        StringComparer.OrdinalIgnoreCase.Equals(registration.InstancePath, r.Candidate.InstancePath) &&
        registration.GameRoot == r.Candidate.GameRoot && registration.VerifiedFiles.SequenceEqual(r.Candidate.Fingerprint) &&
        registration.VerifiedComponents.SequenceEqual(r.Candidate.Components);

    private async Task<ImportAttemptStatus> Failure(ImportOperationRecord record, ImportAttemptState state, string reason, IProgress<ImportAttemptStatus>? progress = null)
    {
        try { return await Set(record, state, reason, progress); }
        catch (Exception error)
        {
            var status = new ImportAttemptStatus(record.OperationId, state, reason + " / 記録保存失敗: " + error.Message, true);
            progress?.Report(status);
            return status;
        }
    }

    private async Task<ImportAttemptStatus> Set(ImportOperationRecord record, ImportAttemptState state, string reason, IProgress<ImportAttemptStatus>? progress = null)
    {
        record.State = state;
        record.Terminal = ImportAttemptStateMachine.IsTerminal(state);
        record.UpdatedAt = clock.GetUtcNow();
        record.Observations.Add(new(record.UpdatedAt, state, reason));
        await store.SaveOperationAsync(record, CancellationToken.None);
        var status = Status(record, reason);
        progress?.Report(status);
        return status;
    }
    private static ImportAttemptStatus Status(ImportOperationRecord record, string reason) => new(record.OperationId, record.State, reason, record.Terminal, record.Readiness);
}
