namespace ModSync.Core.InstanceSetup;

public static class CandidateEvaluator
{
    public static CandidateDecision Evaluate(ImportAttemptPlan plan, InstancesSnapshot before, CandidateFacts[] facts)
    {
        var existing = before.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<CandidateFacts>();
        var unrelated = new List<string>();
        foreach (var f in facts)
        {
            var identity = f.IdentityValid && f.PackId == plan.Definition.PackId && f.OperationId == plan.OperationId && f.IdentitySha256 == plan.IdentitySha256;
            if (existing.Contains(f.InstanceId))
            {
                if (identity) return new(ImportAttemptState.Ambiguous, "preexistingIdentityConflict", null, unrelated.ToArray());
                continue;
            }
            if (f.InstanceId.StartsWith('.')) continue;
            if (!f.SafePath) return new(ImportAttemptState.Unobservable, "unsafePath", null, unrelated.ToArray());
            if (f.Error != null) return new(ImportAttemptState.Unobservable, f.Error, null, unrelated.ToArray());
            if (!identity)
            {
                if (f.IndexSha256 == plan.IndexSha256)
                    return new(ImportAttemptState.Unobservable, f.IdentityPresent ? "identityMismatch" : "identityMissing", null, unrelated.ToArray());
                unrelated.Add(f.InstanceId);
                continue;
            }
            var reason = !f.GameRootValid ? "gameRootAmbiguous" : f.IndexSha256 != plan.IndexSha256 ? "indexMismatch" :
                f.ProbeSha256 != plan.ProbeSha256 ? "probeMismatch" : !f.ComponentsValid || !ComponentsMatch(plan.Definition.Components, f.Components) ? "componentMismatch" : "";
            if (reason.Length > 0) return new(ImportAttemptState.Mismatch, reason, null, unrelated.ToArray());
            matches.Add(f);
        }
        return matches.Count switch
        {
            0 => new(ImportAttemptState.AwaitingUserConfirmation, "noActivity", null, unrelated.ToArray()),
            1 => new(ImportAttemptState.Verified, "stable", matches[0], unrelated.ToArray()),
            _ => new(ImportAttemptState.Ambiguous, "multipleMatches", null, unrelated.ToArray())
        };
    }
    public static bool ComponentsMatch(ExpectedComponent[] expected, ExpectedComponent[] actual)
    {
        string[] forbidden = ["net.minecraftforge", "net.fabricmc.fabric-loader", "org.quiltmc.quilt-loader", "com.mumfrey.liteloader"];
        return expected.All(e => actual.Count(a => a.Uid == e.Uid && a.Version == e.Version) == 1) &&
            actual.Select(a => a.Uid).Distinct(StringComparer.Ordinal).Count() == actual.Length &&
            actual.All(a => !forbidden.Contains(a.Uid) && (expected.Any(e => e.Uid == a.Uid) || a.DependencyOnly));
    }
    public static bool SameFingerprint(CandidateFacts? a, CandidateFacts? b) => a != null && b != null &&
        StringComparer.OrdinalIgnoreCase.Equals(a.InstancePath, b.InstancePath) && a.Fingerprint.SequenceEqual(b.Fingerprint);
}

public static class ReadinessEvaluator
{
    public static ReadinessResult Evaluate(ReadinessFacts facts, DateTimeOffset now) => new(now,
        facts.Mismatch ? ReadinessLevel.Mismatch : facts.Unconfirmed ? ReadinessLevel.Unconfirmed :
        !facts.Declared ? ReadinessLevel.NotChecked : !facts.MetadataResolved ? ReadinessLevel.Declared :
        !facts.FilesPresent ? ReadinessLevel.MetadataResolved : !facts.LoaderReady ? ReadinessLevel.FilesPresent : ReadinessLevel.Ready, facts.Missing);
}

public static class ImportAttemptStateMachine
{
    public static bool IsTerminal(ImportAttemptState state) => state is ImportAttemptState.Registered or ImportAttemptState.AlreadyRegistered or
        ImportAttemptState.TimedOut or ImportAttemptState.StagingVanished or ImportAttemptState.Mismatch or ImportAttemptState.Ambiguous or
        ImportAttemptState.PrerequisiteFailed or ImportAttemptState.Unobservable or ImportAttemptState.CommitFailed or ImportAttemptState.CancelledByApp;
}
