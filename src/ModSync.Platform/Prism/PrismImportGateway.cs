using System.Diagnostics;
using ModSync.Core.InstanceSetup;

namespace ModSync.Platform.Prism;

public interface IPrismProcessLauncher
{
    Task<ImportRequestResult> LaunchAsync(ProcessStartInfo start, CancellationToken ct);
}
public sealed class PrismProcessLauncher : IPrismProcessLauncher
{
    public async Task<ImportRequestResult> LaunchAsync(ProcessStartInfo start, CancellationToken ct)
    {
        var delivery = "newProcess";
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(start.FileName)))
        {
            using (candidate)
            {
                try
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(candidate.MainModule?.FileName, start.FileName)) delivery = "runningInstance";
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { if (delivery != "runningInstance") delivery = "unknown"; }
            }
        }
        var at = DateTimeOffset.UtcNow;
        using var process = Process.Start(start) ?? throw new IOException("取り込み依頼プロセスを開始できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new(at, delivery, process.ExitCode, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(at, delivery, null, null); }
    }
}
public sealed class PrismImportGateway(PrismInstallationFacts installation, IPrismProcessLauncher? launcher = null) : IPrismImportGateway
{
    public PrismInstallationFacts Installation { get; } = installation;
    private readonly IPrismProcessLauncher processLauncher = launcher ?? new PrismProcessLauncher();
    private readonly PrismInstanceReader reader = new();
    public Task<InstancesSnapshot> CaptureSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = SafePathResolver.ResolveExisting(Installation.InstancesRoot);
        var entries = Directory.EnumerateFileSystemEntries(root).Select(p =>
        {
            var attributes = File.GetAttributes(p);
            var reparse = (attributes & FileAttributes.ReparsePoint) != 0;
            var directory = (attributes & FileAttributes.Directory) != 0;
            return new SnapshotEntry(Path.GetFileName(p), reparse ? "reparsePoint" : directory ? "directory" : "file",
                !reparse && directory && File.Exists(Path.Combine(p, "instance.cfg")));
        }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] staging = [];
        if (Directory.Exists(Path.Combine(root, ".tmp")))
            staging = Directory.EnumerateFileSystemEntries(SafePathResolver.ResolveChild(root, ".tmp")).Select(Path.GetFileName).Cast<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.FromResult(new InstancesSnapshot(DateTimeOffset.UtcNow, entries, staging));
    }
    public static ProcessStartInfo CreateStartInfo(string executable, string dataRoot, string archive)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "--dir", dataRoot, "--import", archive }) start.ArgumentList.Add(argument);
        return start;
    }
    public Task<ImportRequestResult> RequestImportAsync(string archivePath, CancellationToken ct) => processLauncher.LaunchAsync(
        CreateStartInfo(SafePathResolver.ResolveExisting(Installation.ExecutablePath), SafePathResolver.ResolveExisting(Installation.DataRoot),
            SafePathResolver.ResolveExisting(archivePath)), ct);
    public async Task<CandidateFacts[]> ObserveCandidatesAsync(InstancesSnapshot before, CancellationToken ct)
    {
        var snapshot = await CaptureSnapshotAsync(ct);
        var facts = new List<CandidateFacts>();
        foreach (var entry in snapshot.Entries.Where(e => !e.Name.StartsWith('.') && e.Kind == "directory" && e.HasInstanceCfg))
        {
            facts.Add(await reader.ReadAsync(Installation.InstancesRoot, entry.Name, ct));
            var path = Path.Combine(Installation.InstancesRoot, entry.Name);
            if (Directory.Exists(Path.Combine(path, "minecraft")) && Directory.Exists(Path.Combine(path, ".minecraft")))
                facts.Add(await reader.ReadAsync(Installation.InstancesRoot, entry.Name, ct, ".minecraft"));
        }
        // 新規リンクは追わず拒否する。既存リンクは候補にならない。
        foreach (var entry in snapshot.Entries.Where(e => e.Kind == "reparsePoint" && !before.Entries.Any(b => StringComparer.OrdinalIgnoreCase.Equals(b.Name, e.Name))))
            facts.Add(new(entry.Name, Path.Combine(Installation.InstancesRoot, entry.Name), "", false, false, false, null, null,
                null, null, null, false, false, [], [], "", "unsafePath"));
        return facts.ToArray();
    }
    public Task<ReadinessFacts> ProbeReadinessAsync(string instancePath, ExpectedComponent[] expected, CancellationToken ct) =>
        new PrismReadinessProbe(Installation.DataRoot).ReadAsync(instancePath, expected, ct);
}
