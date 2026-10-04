using System.Text.Json;
using ModSync.Core.InstanceSetup;
using ModSync.Core.Integrity;

namespace ModSync.Platform.Prism;

public sealed class PrismInstanceReader
{
    public async Task<CandidateFacts> ReadAsync(string instancesRoot, string instanceId, CancellationToken ct, string? gameRootOverride = null)
    {
        var path = Path.Combine(instancesRoot, instanceId);
        string gameRoot = "";
        bool identityPresent = false, identityValid = false, gameRootValid = false, componentsValid = false;
        string? packId = null, identityHash = null, indexHash = null, probeHash = null;
        Guid? operationId = null;
        ExpectedComponent[] components = [];
        var fingerprints = new List<FileFingerprint>();
        string displayName = "";
        try
        {
            path = SafePathResolver.ResolveChild(instancesRoot, instanceId);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), instancesRoot)) throw new IOException("直下のインスタンスではありません。");
            var roots = new[] { "minecraft", ".minecraft" }.Where(r => Directory.Exists(Path.Combine(path, r))).ToArray();
            gameRootValid = roots.Length == 1;
            // 両方存在する場合も識別情報を観測し、不一致として返す。
            gameRoot = gameRootOverride ?? roots.FirstOrDefault() ?? "minecraft";
            if (roots.Length > 0) SafePathResolver.ResolveChild(path, gameRoot);
            var cfg = await ReadBytes(path, "instance.cfg", fingerprints, ct);
            if (cfg != null)
            {
                foreach (var line in System.Text.Encoding.UTF8.GetString(cfg).Split('\n'))
                {
                    if (line.StartsWith("name=", StringComparison.Ordinal)) displayName = line[5..].TrimEnd('\r');
                }
            }
            var identity = await ReadBytes(path, gameRoot + "/modsync/identity.json", fingerprints, ct);
            identityPresent = identity != null;
            if (identity != null)
            {
                identityHash = fingerprints.Last().Sha256;
                try
                {
                    using var doc = JsonDocument.Parse(identity);
                    var j = doc.RootElement;
                    identityValid = j.GetProperty("schemaVersion").GetInt32() == 1 && j.GetProperty("kind").GetString() == "modsync.prism-import-identity" &&
                        j.GetProperty("purpose").GetString() is "verification" or "failureProbe";
                    packId = j.GetProperty("packId").GetString();
                    operationId = j.GetProperty("operationId").GetGuid();
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { identityValid = false; }
            }
            var index = await ReadBytes(path, "mrpack/modrinth.index.json", fingerprints, ct);
            if (index != null) indexHash = fingerprints.Last().Sha256;
            var probe = await ReadBytes(path, gameRoot + "/modsync/probe.txt", fingerprints, ct);
            if (probe != null) probeHash = fingerprints.Last().Sha256;
            var mmc = await ReadBytes(path, "mmc-pack.json", fingerprints, ct);
            if (mmc != null)
            {
                try
                {
                    (componentsValid, components) = ParseComponents(mmc);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { componentsValid = false; }
            }
            return Facts(true, null);
        }
        catch (IOException error) { return Facts(false, "unsafePath: " + error.Message); }
        catch (UnauthorizedAccessException) { return Facts(false, "ioError"); }
        CandidateFacts Facts(bool safe, string? error) => new(instanceId, path, gameRoot, safe, identityPresent, identityValid,
            packId, operationId, identityHash, indexHash, probeHash, gameRootValid, componentsValid, components,
            fingerprints.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(), displayName, error);
    }
    public static (bool Valid, ExpectedComponent[] Components) ParseComponents(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var j = document.RootElement;
        var list = j.GetProperty("components").EnumerateArray().Select(c => new ExpectedComponent(c.GetProperty("uid").GetString()!,
            c.GetProperty("version").GetString()!, "", c.TryGetProperty("dependencyOnly", out var d) && d.GetBoolean())).ToArray();
        return (j.GetProperty("formatVersion").GetInt32() == 1, list);
    }
    private static async Task<byte[]?> ReadBytes(string root, string relative, List<FileFingerprint> fingerprints, CancellationToken ct)
    {
        var raw = Path.Combine(root, relative.Replace('/', '\\'));
        // 存在する親階層も検査する。欠損をリンク経由で判定しない。
        var current = root;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (!Path.Exists(current)) break;
            SafePathResolver.ResolveExisting(current);
        }
        if (!File.Exists(raw)) return null;
        var path = SafePathResolver.ResolveChild(root, relative);
        await using var stream = PrismInstallation.SharedRead(path);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        copy.Position = 0;
        var hash = await ContentHash.ComputeSha256Async(copy, ct);
        fingerprints.Add(new(relative, copy.Length, File.GetLastWriteTimeUtc(path), hash));
        return copy.ToArray();
    }
}
