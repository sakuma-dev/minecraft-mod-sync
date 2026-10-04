using System.Text.Json;
using ModSync.Core.InstanceSetup;

namespace ModSync.Platform.Prism;

public static class ImportInputLoader
{
    public static async Task<ImportInput> LoadAsync(string definitionPath, bool failureProbe = false, CancellationToken ct = default)
    {
        definitionPath = SafePathResolver.ResolveExisting(definitionPath);
        var bytes = await File.ReadAllBytesAsync(definitionPath, ct);
        var definition = JsonSerializer.Deserialize<ImportInputDefinition>(bytes, ImportJson.Options) ?? throw new IOException("入力定義が空です。");
        var templates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var template in definition.TemplateFiles)
        {
            var path = SafePathResolver.ResolveChild(Path.GetDirectoryName(definitionPath)!, template.Source);
            templates.Add(template.Source, await File.ReadAllBytesAsync(path, ct));
        }
        if (failureProbe)
        {
            definition = definition with { Purpose = "failureProbe" };
            bytes = JsonSerializer.SerializeToUtf8Bytes(definition, ImportJson.Options);
        }
        return new(definition, bytes, templates);
    }
}
