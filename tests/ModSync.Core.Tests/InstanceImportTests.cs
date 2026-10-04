using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModSync.Core.InstanceSetup;

namespace ModSync.Core.Tests;

public sealed class InstanceImportTests
{
    private static readonly ImportInputDefinition Definition = new(1, "modsync.prism-import-input-definition", "test-input",
        "eca05ccf-89b3-48c9-a231-8dc58377f611", "verification", "検証", "test-1",
        [new("net.minecraft", "1.21.1", "minecraft"), new("net.neoforged", "21.1.228", "neoforge")], [new("overrides/modsync/probe.txt", "probe.txt")]);
    private static ImportInput Input(ImportInputDefinition? definition = null)
    {
        var d = definition ?? Definition;
        return new(d, JsonSerializer.SerializeToUtf8Bytes(d, ImportJson.Options), new Dictionary<string, byte[]> { ["probe.txt"] = Encoding.UTF8.GetBytes("検証\n") });
    }
    private static InstancesSnapshot Before => new(DateTimeOffset.UnixEpoch, [new("既存", "directory", true), new("空", "directory", false)], []);
    private static CandidateFacts Match(ImportAttemptPlan p, string name = "新規") => new(name, "C:\\Prism\\instances\\" + name, "minecraft", true,
        true, true, p.Definition.PackId, p.OperationId, p.IdentitySha256, p.IndexSha256, p.ProbeSha256, true, true, p.Definition.Components,
        [new("minecraft/modsync/identity.json", 100, DateTimeOffset.UnixEpoch, p.IdentitySha256)], "同じ表示名");
    private static async Task<ImportAttemptPlan> Plan() => (await VerificationPackBuilder.BuildAsync(Input(), Guid.NewGuid())).Plan!;

    [Fact]
    public async Task ArchiveIsDeterministicAndContainsOnlyTheThreeExpectedEntries()
    {
        var id = Guid.NewGuid();
        var a = await VerificationPackBuilder.BuildAsync(Input(), id);
        var b = await VerificationPackBuilder.BuildAsync(Input(), id);
        Assert.Equal(a.Archive, b.Archive);
        Assert.Equal(a.Plan!.ArchiveSha256, b.Plan!.ArchiveSha256);
        using var zip = new ZipArchive(new MemoryStream(a.Archive!));
        Assert.Equal(new[] { "modrinth.index.json", "overrides/modsync/identity.json", "overrides/modsync/probe.txt" }, zip.Entries.Select(e => e.FullName));
        Assert.All(zip.Entries, e => Assert.Equal(1980, e.LastWriteTime.Year));
        using var reader = new StreamReader(zip.Entries[1].Open());
        Assert.DoesNotContain("archiveSha256", await reader.ReadToEndAsync());
    }
    [Theory]
    [InlineData("manifest.json")]
    [InlineData("overrides/instance.cfg")]
    [InlineData("overrides/../probe.txt")]
    [InlineData("/absolute")]
    [InlineData("C:/probe")]
    [InlineData("mods/x")]
    [InlineData("client-overrides/x")]
    [InlineData("overrides\\probe.txt")]
    public async Task UnsafeTemplateIsRejected(string path)
    {
        var result = await VerificationPackBuilder.BuildAsync(Input(Definition with { TemplateFiles = [new(path, "probe.txt")] }), Guid.NewGuid());
        Assert.Null(result.Archive); Assert.NotEmpty(result.Errors);
    }
    [Fact]
    public async Task FailureProbeUsesInvalidGameWithoutChangingVersions()
    {
        var built = await VerificationPackBuilder.BuildAsync(Input(Definition with { Purpose = "failureProbe" }), Guid.NewGuid());
        using var zip = new ZipArchive(new MemoryStream(built.Archive!));
        using var doc = await JsonDocument.ParseAsync(zip.Entries[0].Open());
        Assert.Equal("modsync-invalid", doc.RootElement.GetProperty("game").GetString());
        Assert.Empty(doc.RootElement.GetProperty("files").EnumerateArray());
    }
    [Fact]
    public async Task ExistingIdentityAndMultipleMatchesAreNeverSelected()
    {
        var p = await Plan();
        Assert.Equal(ImportAttemptState.Ambiguous, CandidateEvaluator.Evaluate(p, Before, [Match(p, "既存")]).State);
        Assert.Equal(ImportAttemptState.Ambiguous, CandidateEvaluator.Evaluate(p, Before, [Match(p), Match(p, "複製")]).State);
        var emptyBefore = Before with { Entries = Before.Entries.Append(new("新規", "directory", false)).ToArray() };
        Assert.Equal(ImportAttemptState.Ambiguous, CandidateEvaluator.Evaluate(p, emptyBefore, [Match(p)]).State);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task RetainedIndexNeverSubstitutesForIdentity(bool present, bool valid)
    {
        var p = await Plan();
        var decision = CandidateEvaluator.Evaluate(p, Before, [Match(p) with { IdentityPresent = present, IdentityValid = valid }]);
        Assert.Equal(ImportAttemptState.Unobservable, decision.State);
        Assert.Null(decision.Candidate);
    }
    [Fact]
    public async Task UnrelatedOperationsAreRecordedAndDoNotPreventOneMatch()
    {
        var p = await Plan();
        var other = Match(p, "無関係") with { OperationId = Guid.NewGuid(), IdentitySha256 = "別", IndexSha256 = "別" };
        var decision = CandidateEvaluator.Evaluate(p, Before, [other, Match(p)]);
        Assert.Equal(ImportAttemptState.Verified, decision.State);
        Assert.Equal(new[] { "無関係" }, decision.Unrelated);
    }
    [Fact]
    public async Task AllRequiredComparisonsAreMandatory()
    {
        var p = await Plan(); var good = Match(p);
        foreach (var wrong in new[] { good with { ProbeSha256 = "別" }, good with { IndexSha256 = "別" }, good with { GameRootValid = false }, good with { ComponentsValid = false } })
            Assert.Equal(ImportAttemptState.Mismatch, CandidateEvaluator.Evaluate(p, Before, [wrong]).State);
        Assert.Equal(ImportAttemptState.Unobservable, CandidateEvaluator.Evaluate(p, Before, [good with { SafePath = false }]).State);
        Assert.Equal(ImportAttemptState.Unobservable, CandidateEvaluator.Evaluate(p, Before, [good with { PackId = Guid.NewGuid().ToString() }]).State);
    }
    [Fact]
    public void ComponentDeclarationRejectsMissingMixedAndUnexpectedComponents()
    {
        var expected = Definition.Components;
        Assert.False(CandidateEvaluator.ComponentsMatch(expected, [expected[0]]));
        Assert.False(CandidateEvaluator.ComponentsMatch(expected, [expected[0], expected[1] with { Version = "別" }]));
        foreach (var uid in new[] { "net.minecraftforge", "net.fabricmc.fabric-loader", "org.quiltmc.quilt-loader", "other" })
            Assert.False(CandidateEvaluator.ComponentsMatch(expected, [.. expected, new(uid, "1")]));
        Assert.True(CandidateEvaluator.ComponentsMatch(expected, [.. expected, new("org.lwjgl3", "1", "", true)]));
    }
    [Fact]
    public void ReadinessRequiresEveryStageAndDoesNotHideUnknownHashes()
    {
        Assert.Equal(ReadinessLevel.Declared, ReadinessEvaluator.Evaluate(new(true, false, false, false, false, false, []), DateTimeOffset.UnixEpoch).Level);
        Assert.Equal(ReadinessLevel.MetadataResolved, ReadinessEvaluator.Evaluate(new(true, true, false, false, false, false, []), DateTimeOffset.UnixEpoch).Level);
        Assert.Equal(ReadinessLevel.FilesPresent, ReadinessEvaluator.Evaluate(new(true, true, true, false, false, false, []), DateTimeOffset.UnixEpoch).Level);
        Assert.Equal(ReadinessLevel.Unconfirmed, ReadinessEvaluator.Evaluate(new(true, true, true, true, false, true, []), DateTimeOffset.UnixEpoch).Level);
        Assert.Equal(ReadinessLevel.Mismatch, ReadinessEvaluator.Evaluate(new(true, true, true, true, true, false, []), DateTimeOffset.UnixEpoch).Level);
        Assert.Equal(ReadinessLevel.Ready, ReadinessEvaluator.Evaluate(new(true, true, true, true, false, false, []), DateTimeOffset.UnixEpoch).Level);
    }

    private static ImportWaitOptions Fast => new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(8));
    [Fact]
    public async Task ProcessExitZeroWithoutPlacementTimesOutWithoutRegistration()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { NoCandidate = true };
        var result = await new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast).StartAsync(Input());
        Assert.Equal(ImportAttemptState.TimedOut, result.State); Assert.Null(store.Registration);
        Assert.Equal(0, store.Record!.Request!.ProcessExitCode);
    }
    [Fact]
    public async Task StableNewPlacementRequiresExplicitCommitAndDoubleCommitIsIdempotent()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store);
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var verified = await coordinator.StartAsync(Input());
        Assert.Equal(ImportAttemptState.Verified, verified.State); Assert.Null(store.Registration);
        Assert.Equal(ImportAttemptState.Registered, (await coordinator.RegisterAsync(verified.OperationId)).State);
        var registration = store.Registration;
        Assert.Equal(ImportAttemptState.AlreadyRegistered, (await coordinator.RegisterAsync(verified.OperationId)).State);
        Assert.Same(registration, store.Registration);
    }
    [Fact]
    public async Task ChangeBeforeCommitNeverWritesLedger()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store);
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var result = await coordinator.StartAsync(Input()); gateway.ChangeFingerprint = true;
        Assert.Equal(ImportAttemptState.Writing, (await coordinator.RegisterAsync(result.OperationId)).State);
        Assert.Null(store.Registration);
    }
    [Fact]
    public async Task StagingDisappearanceWithoutPlacementIsNotCalledCancellation()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { NoCandidate = true, VanishingStaging = true };
        var result = await new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast).StartAsync(Input());
        Assert.Equal(ImportAttemptState.StagingVanished, result.State); Assert.Null(store.Registration);
    }
    [Fact]
    public async Task ContinuingCandidateChangesReachTheUnstableLimit()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { ContinuouslyChanging = true };
        var result = await new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast).StartAsync(Input());
        Assert.Equal(ImportAttemptState.TimedOut, result.State); Assert.Equal("unstable", result.Reason);
        Assert.Null(store.Registration);
    }
    [Fact]
    public async Task UnchangingStagingCannotBeRegisteredAndTimesOut()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { KeepStaging = true };
        var result = await new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast).StartAsync(Input());
        Assert.Equal(ImportAttemptState.TimedOut, result.State); Assert.Equal("stagingStalled", result.Reason);
        Assert.Null(store.Registration);
    }
    [Fact]
    public async Task CancelledWaitDoesNotStopPrismOrRegister()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { NoCandidate = true };
        using var cancel = new CancellationTokenSource();
        gateway.OnObserve = () => cancel.Cancel();
        var result = await new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast).StartAsync(Input(), ct: cancel.Token);
        Assert.Equal(ImportAttemptState.CancelledByApp, result.State); Assert.Null(store.Registration);
    }
    [Fact]
    public async Task LateCandidateNeedsRecheckAndExplicitRegistration()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { NoCandidate = true };
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var timedOut = await coordinator.StartAsync(Input()); gateway.NoCandidate = false;
        Assert.Equal(ImportAttemptState.Verified, (await coordinator.RecheckAsync(timedOut.OperationId)).State);
        Assert.Equal("lateCandidateFound", store.Record!.Recheck.Last().Outcome); Assert.Null(store.Registration);
        Assert.Equal(ImportAttemptState.Registered, (await coordinator.RegisterAsync(timedOut.OperationId)).State);
    }
    [Fact]
    public async Task SaveFailureAndConflictingLedgerCannotReportSuccess()
    {
        var store = new MemoryStore { FailCommit = true }; var gateway = new FakeGateway(store);
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var result = await coordinator.StartAsync(Input());
        Assert.Equal(ImportAttemptState.CommitFailed, (await coordinator.RegisterAsync(result.OperationId)).State);
        Assert.Null(store.Registration);
    }
    [Fact]
    public async Task CommittingRecordRecoversOnlyFromTheSameReadBackLedger()
    {
        var store = new MemoryStore { FailRegisteredSave = true }; var gateway = new FakeGateway(store);
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var verified = await coordinator.StartAsync(Input());
        Assert.Equal(ImportAttemptState.CommitFailed, (await coordinator.RegisterAsync(verified.OperationId)).State);
        Assert.Equal(ImportAttemptState.Committing, store.Record!.State);
        Assert.NotNull(store.Registration);
        store.FailRegisteredSave = false;
        Assert.Equal(ImportAttemptState.Registered, (await coordinator.RecheckAsync(verified.OperationId)).State);
        Assert.NotNull(store.Record.RecoveredAt);
    }
    [Fact]
    public async Task ConflictingExistingLedgerIsNotOverwritten()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store);
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var verified = await coordinator.StartAsync(Input());
        Assert.Equal(ImportAttemptState.Registered, (await coordinator.RegisterAsync(verified.OperationId)).State);
        var original = store.Registration!;
        store.Registration = original with { OperationId = Guid.NewGuid(), InstancePath = "C:\\別" };
        Assert.Equal(ImportAttemptState.CommitFailed, (await coordinator.RegisterAsync(verified.OperationId)).State);
        Assert.NotEqual(original.OperationId, store.Registration.OperationId);
    }
    [Fact]
    public async Task UserReportsDoNotMakeAnUnplacedImportSuccessful()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store) { NoCandidate = true };
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        var result = await coordinator.StartAsync(Input());
        await coordinator.ReportUserActionAsync(result.OperationId, "confirmedInPrism");
        Assert.Equal(ImportAttemptState.TimedOut, store.Record!.State);
        Assert.Single(store.Record.UserReports); Assert.Null(store.Registration);
    }
    [Fact]
    public async Task PermanentSaveFailureReturnsNonSuccessWithoutIssuingImport()
    {
        var store = new MemoryStore { FailAllSaves = true }; var gateway = new FakeGateway(store);
        var result = await new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast).StartAsync(Input());
        Assert.Equal(ImportAttemptState.Unobservable, result.State);
        Assert.Equal(0, gateway.Requests); Assert.Null(store.Registration);
    }
    [Fact]
    public async Task UnfinishedOperationAndHeldLockPreventASecondRequest()
    {
        var store = new MemoryStore(); var gateway = new FakeGateway(store);
        var coordinator = new InstanceImportCoordinator(gateway, store, timeProvider: new AutoAdvanceTimeProvider(), waitOptions: Fast);
        await coordinator.StartAsync(Input());
        Assert.Equal(ImportAttemptState.PrerequisiteFailed, (await coordinator.StartAsync(Input())).State);
        Assert.Equal(1, gateway.Requests);
        store.Locked = true;
        Assert.Equal(ImportAttemptState.PrerequisiteFailed, (await coordinator.StartAsync(Input())).State);
        Assert.Equal(1, gateway.Requests);
    }

    private sealed class FakeGateway(MemoryStore store) : IPrismImportGateway
    {
        public PrismInstallationFacts Installation { get; } = new("C:\\Prism\\prism.exe", "C:\\Prism", "C:\\Prism\\instances", "test", "hash", "0123456789abcdef");
        public bool NoCandidate { get; set; }
        public bool ChangeFingerprint { get; set; }
        public bool VanishingStaging { get; set; }
        public bool KeepStaging { get; set; }
        public bool ContinuouslyChanging { get; set; }
        public Action? OnObserve { get; set; }
        public int Requests { get; private set; }
        private int snapshots;
        private int observations;
        public Task<InstancesSnapshot> CaptureSnapshotAsync(CancellationToken ct)
        {
            snapshots++;
            return Task.FromResult(Before with { StagingKeys = KeepStaging && snapshots > 1 || VanishingStaging && snapshots == 2 ? ["work"] : [] });
        }
        public Task<ImportRequestResult> RequestImportAsync(string archivePath, CancellationToken ct)
        { Requests++; return Task.FromResult(new ImportRequestResult(DateTimeOffset.UnixEpoch, "runningInstance", 0, DateTimeOffset.UnixEpoch)); }
        public Task<CandidateFacts[]> ObserveCandidatesAsync(InstancesSnapshot before, CancellationToken ct)
        {
            OnObserve?.Invoke();
            if (NoCandidate) return Task.FromResult(Array.Empty<CandidateFacts>());
            var match = Match(store.Record!.Input!);
            if (ContinuouslyChanging) match = match with { Fingerprint = [new("changing", ++observations, DateTimeOffset.UnixEpoch, "hash")] };
            if (ChangeFingerprint) match = match with { Fingerprint = [new("changed", 0, DateTimeOffset.UnixEpoch, "hash")] };
            return Task.FromResult(new[] { match });
        }
        public Task<ReadinessFacts> ProbeReadinessAsync(string instancePath, ExpectedComponent[] expected, CancellationToken ct) =>
            Task.FromResult(new ReadinessFacts(true, false, false, false, false, false, []));
    }
    private sealed class MemoryStore : IImportRecordStore
    {
        public ImportOperationRecord? Record { get; private set; }
        public RegistrationRecord? Registration { get; set; }
        public bool Locked { get; set; }
        public bool FailCommit { get; set; }
        public bool FailRegisteredSave { get; set; }
        public bool FailAllSaves { get; set; }
        public Task<LockResult> TryAcquireLockAsync(string key, Guid operationId, bool recovery, CancellationToken ct) => Task.FromResult(new LockResult(Locked ? null : new EmptyLock(), false));
        private sealed class EmptyLock : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
        public Task SaveOperationAsync(ImportOperationRecord record, CancellationToken ct)
        {
            if (FailAllSaves || FailRegisteredSave && record.State == ImportAttemptState.Registered) throw new IOException("模擬保存失敗");
            Record = JsonSerializer.Deserialize<ImportOperationRecord>(JsonSerializer.Serialize(record, ImportJson.Options), ImportJson.Options);
            return Task.CompletedTask;
        }
        public Task<ImportOperationRecord?> LoadOperationAsync(string key, Guid operationId, CancellationToken ct) => Task.FromResult(Record == null ? null :
            JsonSerializer.Deserialize<ImportOperationRecord>(JsonSerializer.Serialize(Record, ImportJson.Options), ImportJson.Options));
        public Task<ImportOperationRecord[]> LoadOperationsAsync(string key, CancellationToken ct) => Task.FromResult(Record == null ? Array.Empty<ImportOperationRecord>() : [Record]);
        public Task<string> SaveArchiveAsync(string key, ImportAttemptPlan plan, byte[] archive, CancellationToken ct) => Task.FromResult("fake.mrpack");
        public Task<RegistrationRecord?> LoadRegistrationAsync(string key, string packId, CancellationToken ct) => Task.FromResult(Registration);
        public Task<CommitResult> CommitRegistrationAsync(RegistrationRecord record, CancellationToken ct)
        { if (FailCommit) return Task.FromResult(new CommitResult(false, false, "writeFailed")); Registration = record; return Task.FromResult(new CommitResult(true, false, "matched")); }
    }
    // 実時間の待機や追加パッケージに依存せず、待機上限を進める試験用時計。
    private sealed class AutoAdvanceTimeProvider : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref ticks));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new AutoTimer();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (timer.Disposed) return;
                Interlocked.Add(ref ticks, dueTime.Ticks);
                callback(state);
            });
            return timer;
        }
        private sealed class AutoTimer : ITimer
        {
            public volatile bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
