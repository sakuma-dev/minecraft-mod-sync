using System.IO.Compression;
using System.Text.Json;
using ModSync.Core.InstanceSetup;
using ModSync.Core.Integrity;

namespace ModSync.Platform.Prism;

/// <summary>公開ハッシュ付きの事実だけを採取する。不明な規則・生成物は確認不能のまま返す。</summary>
public sealed class PrismReadinessProbe(string dataRoot)
{
    public async Task<ReadinessFacts> ReadAsync(string instancePath, ExpectedComponent[] expected, CancellationToken ct)
    {
        var missing = new List<MissingFact>();
        bool mismatch = false, unconfirmed = false;
        try
        {
            var mmc = await Bytes(SafePathResolver.ResolveChild(instancePath, "mmc-pack.json"), ct);
            var (valid, components) = PrismInstanceReader.ParseComponents(mmc);
            if (!valid || !CandidateEvaluator.ComponentsMatch(expected, components))
                return new(false, false, false, false, true, false, [new("component", "mmc-pack.json", "hashMismatch")]);
            var documents = new List<JsonDocument>();
            try
            {
                var pending = new Queue<ExpectedComponent>(components);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool metadataResolved = true;
                while (pending.Count > 0)
                {
                    var component = pending.Dequeue();
                    if (!seen.Add(component.Uid + "/" + component.Version)) continue;
                    SafePathResolver.ValidateSegment(component.Uid);
                    SafePathResolver.ValidateSegment(component.Version);
                    var relative = $"meta/{component.Uid}/{component.Version}.json";
                    var path = Path.Combine(dataRoot, relative.Replace('/', '\\'));
                    var indexPath = Path.Combine(dataRoot, "meta", component.Uid, "index.json");
                    if (!File.Exists(path) || !File.Exists(indexPath))
                    {
                        missing.Add(new("metadata", relative, "absent")); metadataResolved = false; continue;
                    }
                    using var index = JsonDocument.Parse(await Bytes(SafePathResolver.ResolveChild(dataRoot, $"meta/{component.Uid}/index.json"), ct));
                    var version = index.RootElement.GetProperty("versions").EnumerateArray().FirstOrDefault(v => v.GetProperty("version").GetString() == component.Version);
                    var bytes = await Bytes(SafePathResolver.ResolveChild(dataRoot, relative), ct);
                    if (version.ValueKind == JsonValueKind.Undefined || !version.TryGetProperty("sha256", out var hash))
                    { missing.Add(new("metadata", relative, "hashUnavailable")); unconfirmed = true; metadataResolved = false; continue; }
                    using var source = new MemoryStream(bytes);
                    if (await ContentHash.ComputeSha256Async(source, ct) != hash.GetString())
                    { missing.Add(new("metadata", relative, "hashMismatch")); mismatch = true; metadataResolved = false; continue; }
                    var document = JsonDocument.Parse(bytes);
                    documents.Add(document);
                    if (document.RootElement.TryGetProperty("requires", out var requires))
                    {
                        foreach (var dependency in requires.EnumerateArray())
                        {
                            var uid = dependency.GetProperty("uid").GetString()!;
                            var resolved = components.FirstOrDefault(c => c.Uid == uid);
                            if (dependency.TryGetProperty("equals", out var exact)) pending.Enqueue(new(uid, exact.GetString()!, "", true));
                            else if (resolved != null) pending.Enqueue(resolved);
                            else { missing.Add(new("metadata", uid, "hashUnavailable")); metadataResolved = false; unconfirmed = true; }
                        }
                    }
                }
                if (!metadataResolved) return new(true, false, false, false, mismatch, unconfirmed, missing.ToArray());
                var artifacts = new List<(JsonElement Json, string Kind)>();
                bool requiresInstaller = false;
                foreach (var doc in documents)
                {
                    var root = doc.RootElement;
                    foreach (var field in new[] { "libraries", "mavenFiles" })
                        if (root.TryGetProperty(field, out var array))
                            foreach (var library in array.EnumerateArray()) artifacts.Add((library, "library"));
                    if (root.TryGetProperty("mainJar", out var main)) artifacts.Add((main, "mainJar"));
                    if ((root.TryGetProperty("mainClass", out var mainClass) && (mainClass.GetString()?.Contains("forgewrapper", StringComparison.OrdinalIgnoreCase) ?? false)) ||
                        root.TryGetProperty("mavenFiles", out _)) requiresInstaller = true;
                }
                if (!artifacts.Any(a => a.Kind == "mainJar"))
                { missing.Add(new("mainJar", "", "hashUnavailable")); unconfirmed = true; }
                var installerPaths = new List<string>();
                bool filesPresent = !unconfirmed;
                foreach (var (artifact, kind) in artifacts)
                {
                    var applicable = AppliesOnWindows(artifact);
                    if (applicable == null) { missing.Add(new(kind, "", "ruleUnknown")); unconfirmed = true; filesPresent = false; continue; }
                    if (!applicable.Value) continue;
                    var coordinate = artifact.GetProperty("name").GetString()!;
                    if (artifact.TryGetProperty("natives", out var natives))
                    {
                        if (!natives.TryGetProperty("windows", out var classifier)) continue;
                        coordinate += ":" + classifier.GetString()!.Replace("${arch}", "64", StringComparison.Ordinal);
                    }
                    var relative = MavenPath(coordinate);
                    if (!artifact.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("artifact", out var download) ||
                        !download.TryGetProperty("sha1", out var sha) || !download.TryGetProperty("size", out var size))
                    { missing.Add(new(kind, relative, "hashUnavailable")); unconfirmed = true; filesPresent = false; continue; }
                    if (artifact.TryGetProperty("natives", out _) && downloads.TryGetProperty("classifiers", out var classifiers))
                    {
                        var classifierKey = coordinate.Split(':').Last();
                        if (!classifiers.TryGetProperty(classifierKey, out download) || !download.TryGetProperty("sha1", out sha) || !download.TryGetProperty("size", out size))
                        { missing.Add(new(kind, relative, "hashUnavailable")); unconfirmed = true; filesPresent = false; continue; }
                    }
                    var raw = Path.Combine(dataRoot, "libraries", relative.Replace('/', '\\'));
                    if (!File.Exists(raw)) { missing.Add(new(kind, relative, "absent")); filesPresent = false; continue; }
                    var path = SafePathResolver.ResolveChild(dataRoot, "libraries/" + relative);
                    if (new FileInfo(path).Length != size.GetInt64())
                    { missing.Add(new(kind, relative, "sizeMismatch")); mismatch = true; filesPresent = false; continue; }
                    await using var file = PrismInstallation.SharedRead(path);
                    if (await ContentHash.ComputeSha1Async(file, ct) != sha.GetString())
                    { missing.Add(new(kind, relative, "hashMismatch")); mismatch = true; filesPresent = false; continue; }
                    if (coordinate.Contains(":installer", StringComparison.Ordinal)) installerPaths.Add(path);
                }
                if (!filesPresent) return new(true, true, false, false, mismatch, unconfirmed, missing.ToArray());
                requiresInstaller |= installerPaths.Count > 0;
                if (!requiresInstaller) return new(true, true, true, true, false, false, []);
                bool loaderReady = installerPaths.Count > 0;
                if (!loaderReady) { missing.Add(new("loaderOutput", "install_profile.json", "hashUnavailable")); unconfirmed = true; }
                foreach (var installer in installerPaths)
                {
                    await using var file = PrismInstallation.SharedRead(installer);
                    using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                    var profile = zip.GetEntry("install_profile.json");
                    if (profile == null) { missing.Add(new("loaderOutput", "install_profile.json", "absent")); loaderReady = false; continue; }
                    await using var profileStream = profile.Open();
                    using var document = await JsonDocument.ParseAsync(profileStream, cancellationToken: ct);
                    var data = document.RootElement.GetProperty("data");
                    var outputs = new List<(string Coordinate, string Sha)>();
                    foreach (var pair in data.EnumerateObject().Where(p => p.Name.EndsWith("_SHA", StringComparison.Ordinal)))
                        if (data.TryGetProperty(pair.Name[..^4], out var artifact))
                            outputs.Add((artifact.GetProperty("client").GetString()!, pair.Value.GetProperty("client").GetString()!));
                    if (document.RootElement.TryGetProperty("processors", out var processors))
                    {
                        foreach (var processor in processors.EnumerateArray())
                        {
                            if (processor.TryGetProperty("sides", out var sides) && !sides.EnumerateArray().Any(s => s.GetString() == "client")) continue;
                            if (processor.TryGetProperty("outputs", out var processorOutputs))
                                foreach (var output in processorOutputs.EnumerateObject())
                                    outputs.Add((ResolveData(output.Name, data), ResolveData(output.Value.GetString()!, data)));
                        }
                    }
                    if (outputs.Count == 0)
                    { missing.Add(new("loaderOutput", "install_profile.json", "hashUnavailable")); loaderReady = false; unconfirmed = true; continue; }
                    foreach (var generatedOutput in outputs.Distinct())
                    {
                    var coordinate = generatedOutput.Coordinate;
                    var expectedSha = generatedOutput.Sha.Trim('\'');
                    if (!coordinate.StartsWith('[') || !coordinate.EndsWith(']') || expectedSha.Length != 40 || expectedSha.Any(c => !char.IsAsciiHexDigit(c)))
                    { missing.Add(new("loaderOutput", "PATCHED", "hashUnavailable")); loaderReady = false; unconfirmed = true; continue; }
                    var relative = MavenPath(coordinate[1..^1]);
                    var raw = Path.Combine(dataRoot, "libraries", relative.Replace('/', '\\'));
                    if (!File.Exists(raw)) { missing.Add(new("loaderOutput", relative, "absent")); loaderReady = false; continue; }
                    await using var output = PrismInstallation.SharedRead(SafePathResolver.ResolveChild(dataRoot, "libraries/" + relative));
                    if (await ContentHash.ComputeSha1Async(output, ct) != expectedSha.ToLowerInvariant())
                    { missing.Add(new("loaderOutput", relative, "hashMismatch")); loaderReady = false; mismatch = true; }
                    }
                }
                return new(true, true, true, loaderReady, mismatch, unconfirmed, missing.ToArray());
            }
            finally { foreach (var document in documents) document.Dispose(); }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            missing.Add(new("metadata", "", "ruleUnknown"));
            return new(false, false, false, false, false, true, missing.ToArray());
        }
    }
    private static string ResolveData(string expression, JsonElement data) => expression.StartsWith('{') && expression.EndsWith('}') &&
        data.TryGetProperty(expression[1..^1], out var value) ? value.GetProperty("client").GetString()! : expression;
    public static string MavenPath(string coordinate)
    {
        var extensionParts = coordinate.Split('@');
        if (extensionParts.Length > 2) throw new IOException("Maven座標が不正です。");
        var parts = extensionParts[0].Split(':');
        if (parts.Length is < 3 or > 4) throw new IOException("Maven座標が不正です。");
        var extension = extensionParts.Length == 2 ? extensionParts[1] : "jar";
        foreach (var p in parts.Concat([extension])) SafePathResolver.ValidateSegment(p);
        var group = parts[0].Split('.');
        foreach (var p in group) SafePathResolver.ValidateSegment(p);
        return string.Join('/', group) + $"/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}" + (parts.Length == 4 ? "-" + parts[3] : "") + "." + extension;
    }
    private static bool? AppliesOnWindows(JsonElement artifact)
    {
        if (!artifact.TryGetProperty("rules", out var rules)) return true;
        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("features", out _)) return null;
            if (rule.EnumerateObject().Any(p => p.Name is not "action" and not "os")) return null;
            bool matches = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("version", out _) || os.EnumerateObject().Any(p => p.Name is not "name" and not "arch")) return null;
                if (os.TryGetProperty("name", out var name)) matches &= name.GetString() == "windows";
                if (os.TryGetProperty("arch", out var arch))
                {
                    if (arch.GetString() is not "x86" and not "x86_64" and not "amd64") return null;
                    matches &= arch.GetString() is "x86_64" or "amd64";
                }
            }
            var action = rule.GetProperty("action").GetString();
            if (action is not "allow" and not "disallow") return null;
            if (matches) allowed = action == "allow";
        }
        return allowed;
    }
    private static async Task<byte[]> Bytes(string path, CancellationToken ct)
    {
        await using var file = PrismInstallation.SharedRead(path);
        using var output = new MemoryStream();
        await file.CopyToAsync(output, ct);
        return output.ToArray();
    }
}
