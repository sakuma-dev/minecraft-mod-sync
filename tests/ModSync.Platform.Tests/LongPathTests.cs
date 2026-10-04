using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModSync.Core.InstanceSetup;
using ModSync.Platform.FileSystem;
using ModSync.Platform.Prism;

namespace ModSync.Platform.Tests;

/// <summary>実環境を使わず、拡張長パスで作った合成ファイルを製品の入口から採取する。</summary>
public sealed class LongPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "modsync-long-tests-" + Guid.NewGuid());
    public LongPathTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static string Extended(string path) => @"\\?\" + path.Replace('/', '\\');
    private string NestedDirectory(string parent, int length)
    {
        var path = parent;
        while (length - path.Length > 101) path = Path.Combine(path, new string('x', 100));
        if (path.Length < length) path = Path.Combine(path, new string('y', length - path.Length - 1));
        Directory.CreateDirectory(Extended(path));
        Assert.Equal(length, path.Length);
        return path;
    }

    [Theory]
    [InlineData(262)]
    [InlineData(267)]
    [InlineData(300)]
    public async Task LongPathsAreNormalizedAndProtectionEvidenceIncludesSizeAttributesAndHash(int length)
    {
        var instances = Path.Combine(root, "instances");
        var instance = Path.Combine(instances, "帰還 合成");
        Directory.CreateDirectory(instance);
        const string name = "DistantHorizons.sqlite";
        var directory = NestedDirectory(Path.Combine(instance, "minecraft", "Distant_Horizons_server_data", "%E3%83%9D_日本語@raid_dimension"), length - name.Length - 1);
        var path = Path.Combine(directory, name);
        var bytes = Encoding.UTF8.GetBytes("合成した保護対象\n");
        await File.WriteAllBytesAsync(Extended(path), bytes);
        Assert.Equal(length, path.Length);
        Assert.Equal(path, SafePathResolver.ResolveExisting(path));
        Assert.Equal(path, SafePathResolver.ResolveExisting(path.ToUpperInvariant()));
        var snapshotter = new InstanceTreeSnapshotter();
        var before = await snapshotter.CaptureAsync(instances, ["帰還 合成"], "beforeImport");
        var entry = Assert.Single(before.Entries, e => e.Path.EndsWith(name, StringComparison.Ordinal));
        Assert.Equal(bytes.LongLength, entry.Size);
        Assert.Equal(File.GetAttributes(Extended(path)).ToString(), entry.Attributes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256);
        Assert.True(InstanceTreeSnapshotter.Compare(before,
            await snapshotter.CaptureAsync(instances, ["帰還 合成"], "afterImport")).Unchanged);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Extended(path)));
    }

    [Fact]
    public async Task LongInstallationPathsPreserveVersionHashAndDataRootKeyWithoutLaunchingTheBinary()
    {
        var directory = NestedDirectory(root, 267);
        var executable = Path.Combine(directory, "synthetic.exe");
        // 試験アセンブリのコピーでPEのバージョン取得だけを確認し、起動しない。
        var source = typeof(LongPathTests).Assembly.Location;
        File.Copy(source, Extended(executable));
        var data = Path.Combine(directory, "data");
        Directory.CreateDirectory(Extended(Path.Combine(data, "instances")));
        var facts = await PrismInstallation.ValidateAsync(executable, data);
        Assert.Equal(FileVersionInfo.GetVersionInfo(source).FileVersion, facts.FileVersion);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source))), facts.ExecutableSha256);
        Assert.Equal(executable, facts.ExecutablePath);
        Assert.Equal(data, facts.DataRoot);
        Assert.Equal(facts.DataRootKey, (await PrismInstallation.ValidateAsync(executable.ToUpperInvariant(), data.ToUpperInvariant())).DataRootKey);
    }

    [Fact]
    public async Task LongInputAndReadinessPathsKeepPublicHashVerification()
    {
        const string packId = "eca05ccf-89b3-48c9-a231-8dc58377f611";
        ExpectedComponent[] components = [new("net.minecraft", "1.21.1", "minecraft"), new("net.neoforged", "21.1.228", "neoforge")];
        var directory = NestedDirectory(root, 267);
        var definition = new ImportInputDefinition(1, "modsync.prism-import-input-definition", "test", packId, "verification",
            "長い入力", "1", components, [new("overrides/modsync/probe.txt", "probe.txt")]);
        var inputPath = Path.Combine(directory, "input-definition.json");
        await File.WriteAllBytesAsync(Extended(inputPath), JsonSerializer.SerializeToUtf8Bytes(definition, ImportJson.Options));
        await File.WriteAllTextAsync(Extended(Path.Combine(directory, "probe.txt")), "合成入力\n");
        var input = await ImportInputLoader.LoadAsync(inputPath);
        Assert.Equal(packId, input.Definition.PackId);
        Assert.Equal(Encoding.UTF8.GetBytes("合成入力\n"), input.Templates["probe.txt"]);

        var instance = Path.Combine(directory, "instances", "合成");
        Directory.CreateDirectory(Extended(instance));
        await File.WriteAllTextAsync(Extended(Path.Combine(instance, "mmc-pack.json")),
            JsonSerializer.Serialize(new { formatVersion = 1, components }, ImportJson.Options));
        var jar = Encoding.UTF8.GetBytes("合成した本体JAR\n");
        var minecraft = JsonSerializer.SerializeToUtf8Bytes(new { uid = "net.minecraft", version = "1.21.1",
            mainJar = new { name = "com.mojang:minecraft:1.21.1:client", downloads = new { artifact = new { size = jar.Length, sha1 = Convert.ToHexStringLower(SHA1.HashData(jar)) } } } });
        var neoforge = JsonSerializer.SerializeToUtf8Bytes(new { uid = "net.neoforged", version = "21.1.228" });
        foreach (var (component, bytes) in new[] { (components[0], minecraft), (components[1], neoforge) })
        {
            var metadata = Path.Combine(directory, "meta", component.Uid);
            Directory.CreateDirectory(Extended(metadata));
            await File.WriteAllBytesAsync(Extended(Path.Combine(metadata, component.Version + ".json")), bytes);
            await File.WriteAllTextAsync(Extended(Path.Combine(metadata, "index.json")), JsonSerializer.Serialize(new {
                versions = new[] { new { version = component.Version, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } } }));
        }
        var library = Path.Combine(directory, "libraries", PrismReadinessProbe.MavenPath("com.mojang:minecraft:1.21.1:client"));
        Directory.CreateDirectory(Extended(Path.GetDirectoryName(library)!));
        await File.WriteAllBytesAsync(Extended(library), jar);
        var probe = new PrismReadinessProbe(directory);
        Assert.Equal(ReadinessLevel.Ready, ReadinessEvaluator.Evaluate(await probe.ReadAsync(instance, components, default), DateTimeOffset.UnixEpoch).Level);
        jar[0] ^= 1;
        await File.WriteAllBytesAsync(Extended(library), jar);
        Assert.Equal(ReadinessLevel.Mismatch, ReadinessEvaluator.Evaluate(await probe.ReadAsync(instance, components, default), DateTimeOffset.UnixEpoch).Level);
    }

    [Fact]
    public async Task LongCandidatePathsKeepExistingExclusionAndStoreRecordsWithoutReassignment()
    {
        const string key = "0123456789abcdef";
        const string packId = "eca05ccf-89b3-48c9-a231-8dc58377f611";
        ExpectedComponent[] components = [new("net.minecraft", "1.21.1", "minecraft"), new("net.neoforged", "21.1.228", "neoforge")];
        var definition = new ImportInputDefinition(1, "modsync.prism-import-input-definition", "test", packId, "verification",
            "長いパス", "1", components, [new("overrides/modsync/probe.txt", "probe.txt")]);
        var built = await VerificationPackBuilder.BuildAsync(new(definition, JsonSerializer.SerializeToUtf8Bytes(definition, ImportJson.Options),
            new Dictionary<string, byte[]> { ["probe.txt"] = Encoding.UTF8.GetBytes("合成入力\n") }), Guid.NewGuid());
        var plan = built.Plan!;
        var data = NestedDirectory(Path.Combine(root, "data"), 240);
        var instances = Path.Combine(data, "instances");
        async Task Place(string id)
        {
            var instance = Path.Combine(instances, id);
            Directory.CreateDirectory(Extended(instance));
            await File.WriteAllTextAsync(Extended(Path.Combine(instance, "instance.cfg")), "name=同名\n");
            await File.WriteAllTextAsync(Extended(Path.Combine(instance, "mmc-pack.json")),
                JsonSerializer.Serialize(new { formatVersion = 1, components }, ImportJson.Options));
            using var zip = new ZipArchive(new MemoryStream(built.Archive!));
            foreach (var entry in zip.Entries)
            {
                var relative = entry.FullName == "modrinth.index.json" ? "mrpack/modrinth.index.json" : entry.FullName.Replace("overrides/", "minecraft/", StringComparison.Ordinal);
                var path = Path.Combine(instance, relative);
                Directory.CreateDirectory(Extended(Path.GetDirectoryName(path)!));
                await using var source = entry.Open();
                await using var target = File.Create(Extended(path));
                await source.CopyToAsync(target);
            }
        }
        await Place("既存");
        var installation = new PrismInstallationFacts(Path.Combine(data, "synthetic.exe"), data,
            SafePathResolver.ResolveExisting(instances), "test", "hash", key);
        var gateway = new PrismImportGateway(installation);
        var before = await gateway.CaptureSnapshotAsync(default);
        var existing = await new PrismInstanceReader().ReadAsync(installation.InstancesRoot, "既存", default);
        Assert.True(existing.SafePath, existing.Error);
        Assert.Equal("preexistingIdentityConflict", CandidateEvaluator.Evaluate(plan, before, [existing]).Reason);
        var existingIdentity = Path.Combine(existing.InstancePath, "minecraft", "modsync", "identity.json");
        var existingText = await File.ReadAllTextAsync(Extended(existingIdentity));
        await File.WriteAllTextAsync(Extended(existingIdentity), existingText.Replace(plan.OperationId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal));
        var originalExisting = await File.ReadAllBytesAsync(Extended(existingIdentity));
        await Place("新規");
        var candidate = await new PrismInstanceReader().ReadAsync(installation.InstancesRoot, "新規", default);
        Assert.True(candidate.SafePath, candidate.Error);
        Assert.Equal(plan.IdentitySha256, candidate.IdentitySha256);
        var observed = await gateway.ObserveCandidatesAsync(before, default);
        var decision = CandidateEvaluator.Evaluate(plan, before, observed);
        Assert.Equal(ImportAttemptState.Verified, decision.State);
        Assert.Equal("新規", decision.Candidate!.InstanceId);
        Assert.Equal(originalExisting, await File.ReadAllBytesAsync(Extended(existingIdentity)));
        Assert.All(candidate.Fingerprint, f => Assert.True(Path.Combine(candidate.InstancePath, f.Path).Length > 260));

        var app = NestedDirectory(Path.Combine(root, "app"), 240);
        var store = new JsonImportRecordStore(app, prismDataRoot: data);
        var acquired = await store.TryAcquireLockAsync(key, plan.OperationId, false, default);
        Assert.NotNull(acquired.Handle);
        await acquired.Handle!.DisposeAsync();
        var archive = await store.SaveArchiveAsync(key, plan, built.Archive!, default);
        Assert.True(archive.Length > 300);
        Assert.Equal(built.Archive, await File.ReadAllBytesAsync(Extended(archive)));
        var operation = new ImportOperationRecord { OperationId = plan.OperationId, PackId = packId, Prism = installation,
            Input = plan, Before = before, Candidate = candidate, State = ImportAttemptState.Verified };
        await store.SaveOperationAsync(operation, default);
        Assert.Equal(candidate.InstancePath, (await store.LoadOperationAsync(key, plan.OperationId, default))!.Candidate!.InstancePath);
        Assert.Single(await store.LoadOperationsAsync(key, default));
        var record = new RegistrationRecord(1, "modsync.prism-instance-registration", packId, plan.OperationId, "verification", data,
            key, "新規", candidate.InstancePath, "minecraft", "同名", plan, components, candidate.Fingerprint, ReadinessLevel.Declared, DateTimeOffset.UnixEpoch);
        Assert.True((await store.CommitRegistrationAsync(record, default)).Success);
        Assert.Equal(candidate.InstancePath, (await store.LoadRegistrationAsync(key, packId, default))!.InstancePath);
        Assert.False((await store.CommitRegistrationAsync(record with { InstancePath = existing.InstancePath }, default)).Success);
        Assert.Equal(candidate.InstancePath, (await store.LoadRegistrationAsync(key, packId, default))!.InstancePath);
        Assert.Empty(Directory.EnumerateFiles(Extended(app), "*.tmp-*", SearchOption.AllDirectories));

        var snapshot = await new InstanceTreeSnapshotter().CaptureAsync(instances, ["既存"], "beforeImport");
        var copied = Path.Combine(app, "prism-import", key, "operations", plan.OperationId.ToString(), "measurement.json");
        // 実測記録の保存先はデータルートキー直下とする。
        var keyedEvidence = Path.Combine(app, "prism-import", key, "measurement.json");
        await InstanceTreeSnapshotter.AppendEvidenceAsync(keyedEvidence, snapshot, "prismAlreadyRunning", true);
        await InstanceTreeSnapshotter.AttachOperationAsync(keyedEvidence, plan.OperationId, "test");
        Assert.True(File.Exists(Extended(copied)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\outside")]
    [InlineData("\\\\?\\UNC\\server\\share")]
    [InlineData("\\\\.\\C:\\outside")]
    [InlineData("safe//child")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("safe:stream")]
    [InlineData("CON.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void LongRootsStillRejectUnsafeChildren(string relative)
    {
        var directory = NestedDirectory(root, 267);
        Assert.Throws<IOException>(() => SafePathResolver.ResolveChild(directory, relative));
    }

    [Fact]
    public void DriveRootAndLongChildKeepTheSameCanonicalRepresentation()
    {
        var drive = Path.GetPathRoot(root)!;
        Assert.Equal(drive, SafePathResolver.ResolveExisting(drive));
        var directory = NestedDirectory(root, 267);
        Assert.Equal(directory, SafePathResolver.ResolveChild(drive, Path.GetRelativePath(drive, directory)));
        Assert.Throws<IOException>(() => SafePathResolver.ResolveExisting(Extended(directory)));
    }

    [Fact]
    public async Task LongPathsStillRejectAReparsePointInAnAncestor()
    {
        var directory = NestedDirectory(root, 267);
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(directory, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(Extended(link)); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(target, "outside.txt"), "合成した領域外ファイル\n");
            Assert.Throws<IOException>(() => SafePathResolver.ResolveExisting(Path.Combine(link, "outside.txt")));
            await Assert.ThrowsAsync<IOException>(() => new InstanceTreeSnapshotter().CaptureAsync(directory, ["junction"], "beforeImport"));
        }
        finally { Directory.Delete(Extended(link)); }
    }

    [Fact]
    public async Task LongPathsStreamLargeSharedFilesAndFailClosedOnExclusiveLocks()
    {
        var instances = Path.Combine(root, "instances");
        var directory = NestedDirectory(Path.Combine(instances, "合成", "minecraft"), 267);
        var path = Path.Combine(directory, "large.sqlite");
        var bytes = new byte[16 * 1024 * 1024];
        new Random(12).NextBytes(bytes);
        await File.WriteAllBytesAsync(Extended(path), bytes);
        var snapshotter = new InstanceTreeSnapshotter();
        using (var shared = new FileStream(Extended(path), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var snapshot = await snapshotter.CaptureAsync(instances, ["合成"], "beforeImport");
            var entry = Assert.Single(snapshot.Entries, e => e.Kind == "file");
            Assert.Equal(bytes.LongLength, entry.Size);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256);
        }
        using (var locked = new FileStream(Extended(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => snapshotter.CaptureAsync(instances, ["合成"], "beforeImport"));
        // 認証名の合成ファイルは長いパスでも排他ロックしたまま除外できる。
        var excluded = Path.Combine(directory, "accounts.json");
        await File.WriteAllTextAsync(Extended(excluded), "合成入力・実認証情報なし\n");
        using var credentialsLock = new FileStream(Extended(excluded), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var final = await snapshotter.CaptureAsync(instances, ["合成"], "afterImport");
        Assert.DoesNotContain(final.Entries, e => e.Path.EndsWith("accounts.json", StringComparison.Ordinal));
        Assert.Contains(final.Excluded, p => p.EndsWith("accounts.json", StringComparison.Ordinal));
    }
}
