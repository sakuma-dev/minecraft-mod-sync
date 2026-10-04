using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace ModSync.Core.InstanceSetup;

public enum ImportAttemptState
{
    Prepared, ImportRequested, HandedOffToRunningPrism, AwaitingUserConfirmation, Writing,
    Verified, Committing, Registered, AlreadyRegistered, TimedOut, StagingVanished, Mismatch,
    Ambiguous, PrerequisiteFailed, Unobservable, CommitFailed, CancelledByApp
}

public enum ReadinessLevel { NotChecked, Declared, MetadataResolved, FilesPresent, Ready, Mismatch, Unconfirmed }
public sealed record ExpectedComponent(string Uid, string Version, string MrpackDependency = "", bool DependencyOnly = false);
public sealed record TemplateFile(string ArchivePath, string Source);
public sealed record ImportInputDefinition(int SchemaVersion, string Kind, string InputSetId, string PackId,
    string Purpose, string DisplayNamePrefix, string VersionId, ExpectedComponent[] Components, TemplateFile[] TemplateFiles);
public sealed record ImportInput(ImportInputDefinition Definition, byte[] DefinitionBytes, IReadOnlyDictionary<string, byte[]> Templates);
public sealed record ImportAttemptPlan(Guid OperationId, ImportInputDefinition Definition, string ArchiveFileName,
    string ArchiveSha256, string TemplateSha256, string IndexSha256, string IdentitySha256, string ProbeSha256,
    string ExpectedDisplayName);
public sealed record PackBuildResult(ImportAttemptPlan? Plan, byte[]? Archive, string[] Errors);
public sealed record PrismInstallationFacts(string ExecutablePath, string DataRoot, string InstancesRoot,
    string FileVersion, string ExecutableSha256, string DataRootKey);
public sealed record SnapshotEntry(string Name, string Kind, bool HasInstanceCfg);
public sealed record InstancesSnapshot(DateTimeOffset CapturedAt, SnapshotEntry[] Entries, string[] StagingKeys);
public sealed record FileFingerprint(string Path, long Size, DateTimeOffset LastWriteUtc, string Sha256);
public sealed record CandidateFacts(string InstanceId, string InstancePath, string GameRoot, bool SafePath,
    bool IdentityPresent, bool IdentityValid, string? PackId, Guid? OperationId, string? IdentitySha256,
    string? IndexSha256, string? ProbeSha256, bool GameRootValid, bool ComponentsValid,
    ExpectedComponent[] Components, FileFingerprint[] Fingerprint, string DisplayName, string? Error = null);
public sealed record CandidateDecision(ImportAttemptState State, string Reason, CandidateFacts? Candidate, string[] Unrelated);
public sealed record ImportRequestResult(DateTimeOffset RequestedAt, string Delivery, int? ProcessExitCode, DateTimeOffset? ProcessExitedAt);
public sealed record MissingFact(string Kind, string Path, string Reason);
public sealed record ReadinessFacts(bool Declared, bool MetadataResolved, bool FilesPresent, bool LoaderReady,
    bool Mismatch, bool Unconfirmed, MissingFact[] Missing);
public sealed record ReadinessResult(DateTimeOffset CheckedAt, ReadinessLevel Level, MissingFact[] Missing);
public sealed record ImportAttemptStatus(Guid OperationId, ImportAttemptState State, string Reason, bool Terminal, ReadinessResult? Readiness = null);
public sealed record Observation(DateTimeOffset At, ImportAttemptState State, string Reason);
public sealed record UserAction(DateTimeOffset At, string Report);
public sealed record RecheckResult(DateTimeOffset At, string Outcome);
public sealed class ImportOperationRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "modsync.prism-import-operation";
    public Guid OperationId { get; set; }
    public string PackId { get; set; } = "";
    public string InputSetId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public PrismInstallationFacts Prism { get; set; } = null!;
    public string DataRoot => Prism.DataRoot;
    public string DataRootKey => Prism.DataRootKey;
    public string InstancesRoot => Prism.InstancesRoot;
    public ImportAttemptPlan? Input { get; set; }
    public InstancesSnapshot? Before { get; set; }
    public ImportRequestResult? Request { get; set; }
    public List<Observation> Observations { get; set; } = [];
    public CandidateFacts? Candidate { get; set; }
    public string[] UnrelatedNewEntries { get; set; } = [];
    public ReadinessResult? Readiness { get; set; }
    public List<UserAction> UserReports { get; set; } = [];
    public List<RecheckResult> Recheck { get; set; } = [];
    public ImportAttemptState State { get; set; }
    public bool Terminal { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed record RegistrationRecord(int SchemaVersion, string Kind, string PackId, Guid OperationId,
    string Purpose, string DataRoot, string DataRootKey, string InstanceId, string InstancePath,
    string GameRoot, string DisplayNameAtRegistration, ImportAttemptPlan Input, ExpectedComponent[] VerifiedComponents,
    FileFingerprint[] VerifiedFiles, ReadinessLevel ReadinessAtRegistration, DateTimeOffset RegisteredAt);
public sealed record CommitResult(bool Success, bool AlreadyRegistered, string Reason);
public sealed record LockResult(IAsyncDisposable? Handle, bool Stale);

public interface IPrismImportGateway
{
    PrismInstallationFacts Installation { get; }
    Task<InstancesSnapshot> CaptureSnapshotAsync(CancellationToken ct);
    Task<ImportRequestResult> RequestImportAsync(string archivePath, CancellationToken ct);
    Task<CandidateFacts[]> ObserveCandidatesAsync(InstancesSnapshot before, CancellationToken ct);
    Task<ReadinessFacts> ProbeReadinessAsync(string instancePath, ExpectedComponent[] expected, CancellationToken ct);
}
public interface IImportRecordStore
{
    Task<LockResult> TryAcquireLockAsync(string key, Guid operationId, bool recovery, CancellationToken ct);
    Task SaveOperationAsync(ImportOperationRecord record, CancellationToken ct);
    Task<ImportOperationRecord?> LoadOperationAsync(string key, Guid operationId, CancellationToken ct);
    Task<ImportOperationRecord[]> LoadOperationsAsync(string key, CancellationToken ct);
    Task<string> SaveArchiveAsync(string key, ImportAttemptPlan plan, byte[] archive, CancellationToken ct);
    Task<RegistrationRecord?> LoadRegistrationAsync(string key, string packId, CancellationToken ct);
    Task<CommitResult> CommitRegistrationAsync(RegistrationRecord record, CancellationToken ct);
}

public static class ImportJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        options.Converters.Add(new JsonStringEnumConverter<ImportAttemptState>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ReadinessLevel>(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}
