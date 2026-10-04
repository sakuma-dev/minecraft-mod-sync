using System.Text.Json;
using ModSync.Core.InstanceSetup;
using ModSync.Core.Integrity;
using ModSync.Platform.Prism;

namespace ModSync.Platform.FileSystem;

public sealed record TreeEntry(string Path, string Kind, long Size, string Attributes, string? Sha256);
public sealed record TreeSnapshot(string Stage, DateTimeOffset CapturedAt, string[] Scope, string[] Excluded, TreeEntry[] Entries, string? SharedGroupsSha256);
public sealed record TreeDifference(string Path, string Change);
public sealed record TreeComparison(string From, string To, bool Unchanged, TreeDifference[] Differences);
public sealed class ProtectedMeasurement
{
    public string BaselineKind { get; set; } = "unavailable";
    public bool PrismRunningAtStart { get; set; }
    public List<TreeSnapshot> Snapshots { get; set; } = [];
    public List<TreeComparison> Comparisons { get; set; } = [];
    public List<string> UnconfirmedRanges { get; set; } = [];
    public string OverallResult { get; set; } = "notCompared";
    public List<string> SharedChanges { get; set; } = [];
}
public sealed class PrismImportMeasurement
{
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "modsync.prism-import-measurement";
    public List<Guid> OperationIds { get; set; } = [];
    public string CodeCommitSha { get; set; } = "unconfirmed";
    public Dictionary<string, string> Environment { get; set; } = [];
    public string Operator { get; set; } = "hub";
    public List<Dictionary<string, string>> UserActions { get; set; } = [];
    public ProtectedMeasurement ProtectedComparison { get; set; } = new();
    public List<Dictionary<string, object>> Conditions { get; set; } = Enumerable.Range(1, 6)
        .Select(id => new Dictionary<string, object> { ["id"] = id, ["result"] = "unconfirmed", ["evidence"] = Array.Empty<string>() }).ToList();
    public string[] EvidencePaths { get; set; } = [];
    public bool Anonymized { get; set; }
}

/// <summary>保護対象の構造と非機密ファイルを読む。リンクは追わず、認証ファイルを開かない。</summary>
public sealed class InstanceTreeSnapshotter
{
    private static readonly string[] excludedNames = ["accounts.json", "prismlauncher.cfg", "credentials.json", "tokens.json", "auth.json", ".env"];
    public async Task<TreeSnapshot> CaptureAsync(string instancesRoot, string[] scope, string stage, CancellationToken ct = default)
    {
        if (scope.Length == 0 || scope.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scope.Length) throw new IOException("保護対象を明示してください。");
        var entries = new List<TreeEntry>();
        var excluded = new List<string>(excludedNames);
        foreach (var name in scope.Order(StringComparer.OrdinalIgnoreCase))
        {
            SafePathResolver.ValidateSegment(name);
            var path = SafePathResolver.ResolveChild(instancesRoot, name);
            if (!Directory.Exists(path)) throw new IOException("保護対象がディレクトリではありません。");
            await Walk(path, name);
        }
        string? sharedHash = null;
        var groups = Path.Combine(instancesRoot, "instgroups.json");
        if (File.Exists(groups))
        {
            await using var file = PrismInstallation.SharedRead(groups);
            sharedHash = await ContentHash.ComputeSha256Async(file, ct);
        }
        return new(stage, DateTimeOffset.UtcNow, scope, excluded.ToArray(), entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray(), sharedHash);

        async Task Walk(string path, string relative)
        {
            ct.ThrowIfCancellationRequested();
            SafePathResolver.RejectReparse(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                entries.Add(new(relative, "directory", 0, attributes.ToString(), null));
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(child);
                    if (Excluded(name)) { excluded.Add(relative + "/" + name); continue; }
                    await Walk(child, relative + "/" + name);
                }
            }
            else
            {
                await using var file = PrismInstallation.SharedRead(path);
                entries.Add(new(relative, "file", file.Length, attributes.ToString(), await ContentHash.ComputeSha256Async(file, ct)));
            }
        }
    }
    private static bool Excluded(string name) => excludedNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("credentials", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("tokens", StringComparison.OrdinalIgnoreCase) || name.StartsWith("auth", StringComparison.OrdinalIgnoreCase) ||
        name is "secrets" || new[] { ".key", ".pfx", ".p12" }.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static TreeComparison Compare(TreeSnapshot before, TreeSnapshot after)
    {
        if (!before.Scope.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(after.Scope.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("比較対象が異なります。");
        var a = before.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var b = after.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var differences = new List<TreeDifference>();
        foreach (var path in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!a.TryGetValue(path, out var old)) differences.Add(new(path, "added"));
            else if (!b.TryGetValue(path, out var current)) differences.Add(new(path, "removed"));
            else if (old.Kind != current.Kind || old.Size != current.Size || old.Sha256 != current.Sha256) differences.Add(new(path, "modified"));
            else if (old.Attributes != current.Attributes) differences.Add(new(path, "attributesChanged"));
        }
        return new(before.Stage, after.Stage, differences.Count == 0, differences.ToArray());
    }
    public static async Task<ProtectedMeasurement> AppendEvidenceAsync(string evidencePath, TreeSnapshot snapshot,
        string baselineKind, bool prismRunningAtStart, CancellationToken ct = default)
    {
        PrismImportMeasurement document;
        if (File.Exists(evidencePath))
        {
            await using var file = PrismInstallation.SharedRead(evidencePath);
            document = await JsonSerializer.DeserializeAsync<PrismImportMeasurement>(file, ImportJson.Options, ct) ?? throw new IOException("証拠が空です。");
            if (document.SchemaVersion != 1 || document.Kind != "modsync.prism-import-measurement") throw new IOException("実測記録の形式が不正です。");
        }
        else
        {
            document = new() { ProtectedComparison = new() { BaselineKind = baselineKind, PrismRunningAtStart = prismRunningAtStart },
                CodeCommitSha = System.Environment.GetEnvironmentVariable("MODSYNC_VERIFICATION_COMMIT") ?? "unconfirmed",
                Environment = new() { ["os"] = System.Environment.OSVersion.ToString(), ["dotnetSdk"] = System.Environment.GetEnvironmentVariable("MODSYNC_VERIFICATION_SDK") ?? "unconfirmed" },
                EvidencePaths = [evidencePath] };
            if (baselineKind == "prismAlreadyRunning") document.ProtectedComparison.UnconfirmedRanges.Add("Prism起動時から現在の基準までの変化は未確認です。");
        }
        var measurement = document.ProtectedComparison;
        if (measurement.Snapshots.Count > 0)
        {
            var last = measurement.Snapshots[^1];
            measurement.Comparisons.Add(Compare(last, snapshot));
            if (measurement.Snapshots.Count > 1) measurement.Comparisons.Add(Compare(measurement.Snapshots[0], snapshot));
            if (last.SharedGroupsSha256 != snapshot.SharedGroupsSha256) measurement.SharedChanges.Add($"instances/instgroups.json: {snapshot.Stage}");
        }
        measurement.Snapshots.Add(snapshot);
        measurement.OverallResult = measurement.Comparisons.Any(c => !c.Unchanged) ? "changed" : measurement.Comparisons.Count > 0 ? "unchangedInObservedStages" : "notCompared";
        var parent = Path.GetDirectoryName(evidencePath)!;
        Directory.CreateDirectory(parent);
        SafePathResolver.ResolveExisting(parent);
        var temporary = evidencePath + ".tmp-" + Guid.NewGuid();
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, document, ImportJson.Options, ct);
                file.Flush(true);
            }
            File.Move(temporary, evidencePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return measurement;
    }
    public static async Task AttachOperationAsync(string evidencePath, Guid operationId, string prismVersion, CancellationToken ct = default)
    {
        var directory = Path.Combine(Path.GetDirectoryName(evidencePath)!, "operations", operationId.ToString());
        if (!File.Exists(Path.Combine(directory, "operation.json"))) return;
        SafePathResolver.ResolveExisting(directory);
        await using var input = PrismInstallation.SharedRead(evidencePath);
        var document = await JsonSerializer.DeserializeAsync<PrismImportMeasurement>(input, ImportJson.Options, ct) ?? throw new IOException("実測記録が空です。");
        if (!document.OperationIds.Contains(operationId)) document.OperationIds.Add(operationId);
        document.Environment["prismFileVersion"] = prismVersion;
        var path = Path.Combine(directory, "measurement.json");
        var temporary = path + ".tmp-" + Guid.NewGuid();
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, document, ImportJson.Options, ct);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
