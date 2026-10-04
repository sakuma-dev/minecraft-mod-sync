using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModSync.Core.Integrity;

namespace ModSync.Core.InstanceSetup;

public static class VerificationPackBuilder
{
    public static bool IsSafeArchivePath(string path) => !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split('/').All(s => s.Length > 0 && s is not "." and not ".." && !s.EndsWith('.') && !s.EndsWith(' ')) &&
        !path.Split('/').Any(s => s.Equals("instance.cfg", StringComparison.OrdinalIgnoreCase)) &&
        !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
        !path.Split('/').Any(s => s.Equals("mods", StringComparison.OrdinalIgnoreCase)) &&
        !path.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase);

    public static async Task<PackBuildResult> BuildAsync(ImportInput input, Guid operationId, CancellationToken ct = default)
    {
        var d = input.Definition;
        var errors = new List<string>();
        if (d.SchemaVersion != 1 || d.Kind != "modsync.prism-import-input-definition") errors.Add("入力定義の形式が未対応です。");
        if (!Guid.TryParseExact(d.PackId, "D", out var pack) || pack.ToString() != d.PackId || d.PackId[14] != '4' || !"89ab".Contains(d.PackId[19])) errors.Add("packIdは小文字UUIDv4が必要です。");
        if (operationId == Guid.Empty || operationId.ToString()[14] != '4' || !"89ab".Contains(operationId.ToString()[19])) errors.Add("operationIdはUUIDv4が必要です。");
        if (d.Purpose is not "verification" and not "failureProbe") errors.Add("用途が未対応です。");
        if (!IsSafeArchivePath(d.InputSetId) || d.InputSetId.Contains('/')) errors.Add("inputSetIdが不正です。");
        if (d.Components.Length != 2 || d.Components.Count(c => c.Uid == "net.minecraft" && c.MrpackDependency == "minecraft") != 1 ||
            d.Components.Count(c => c.Uid == "net.neoforged" && c.MrpackDependency == "neoforge") != 1 ||
            d.Components.Any(c => string.IsNullOrWhiteSpace(c.Version) || c.DependencyOnly)) errors.Add("MinecraftとNeoForgeだけを指定してください。");
        if (d.TemplateFiles.Length != 1 || d.TemplateFiles[0].ArchivePath != "overrides/modsync/probe.txt" ||
            d.TemplateFiles.Any(t => !IsSafeArchivePath(t.ArchivePath) || !IsSafeArchivePath(t.Source) || !input.Templates.ContainsKey(t.Source))) errors.Add("固定検証ファイルの指定が不正です。");
        if (errors.Count > 0) return new(null, null, errors.ToArray());
        var name = d.DisplayNamePrefix + " " + operationId.ToString()[..8];
        var identity = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "modsync.prism-import-identity",
            packId = d.PackId, operationId = operationId.ToString(), inputSetId = d.InputSetId, purpose = d.Purpose }, ImportJson.Options);
        var dependencies = d.Components.OrderBy(c => c.MrpackDependency == "minecraft" ? 0 : 1).ToDictionary(c => c.MrpackDependency, c => c.Version);
        var index = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1,
            game = d.Purpose == "failureProbe" ? "modsync-invalid" : "minecraft", versionId = d.VersionId, name,
            summary = "Minecraft MOD Syncの取り込み検証用。MODを含まない。", files = Array.Empty<object>(), dependencies }, ImportJson.Options);
        var probe = input.Templates[d.TemplateFiles[0].Source];
        var probeHash = await Hash(probe, ct);
        using var template = new MemoryStream();
        var list = Encoding.UTF8.GetBytes($"{d.TemplateFiles[0].ArchivePath}\t{probeHash}\n");
        await template.WriteAsync(list, ct);
        await template.WriteAsync(input.DefinitionBytes, ct);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (path, bytes) in new[] { ("modrinth.index.json", index), ("overrides/modsync/identity.json", identity), ("overrides/modsync/probe.txt", probe) })
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, ct);
            }
        }
        var archive = output.ToArray();
        return new(new(operationId, d, $"modsync-{d.InputSetId}-{operationId.ToString()[..8]}.mrpack",
            await Hash(archive, ct), await Hash(template.ToArray(), ct), await Hash(index, ct), await Hash(identity, ct), probeHash, name), archive, []);
    }

    private static async Task<string> Hash(byte[] bytes, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes);
        return await ContentHash.ComputeSha256Async(stream, ct);
    }
}
