using System.Diagnostics;
using System.Text;
using ModSync.Core.InstanceSetup;
using ModSync.Core.Integrity;

namespace ModSync.Platform.Prism;

public static class PrismInstallation
{
    public static async Task<PrismInstallationFacts> ValidateAsync(string executable, string dataRoot, CancellationToken ct = default)
    {
        executable = SafePathResolver.ResolveExisting(executable);
        dataRoot = SafePathResolver.ResolveExisting(dataRoot);
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Prism実行ファイルを明示してください。");
        var instances = SafePathResolver.ResolveChild(dataRoot, "instances");
        if (!Directory.Exists(instances)) throw new IOException("instancesがディレクトリではありません。");
        using var key = new MemoryStream(Encoding.UTF8.GetBytes(dataRoot.ToUpperInvariant()));
        await using var binary = SharedRead(executable);
        return new(executable, dataRoot, instances, FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "unknown",
            await ContentHash.ComputeSha256Async(binary, ct), (await ContentHash.ComputeSha256Async(key, ct))[..16]);
    }
    public static FileStream SharedRead(string path) => new(SafePathResolver.ResolveExisting(path), FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
