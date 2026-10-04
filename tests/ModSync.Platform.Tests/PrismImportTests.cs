using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModSync.Core.InstanceSetup;
using ModSync.Core.Integrity;
using ModSync.Platform.FileSystem;
using ModSync.Platform.Prism;

namespace ModSync.Platform.Tests;

public sealed class PrismImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "modsync-prism-tests-" + Guid.NewGuid());
    private const string Key = "0123456789abcdef";
    private const string PackId = "eca05ccf-89b3-48c9-a231-8dc58377f611";
    private static ExpectedComponent[] Components => [new("net.minecraft", "1.21.1", "minecraft"), new("net.neoforged", "21.1.228", "neoforge")];
    public PrismImportTests() { Directory.CreateDirectory(root); }
    public void Dispose() { Directory.Delete(root, true); }
    private string DirectoryAt(string relative)
    { var path = Path.Combine(root, relative); Directory.CreateDirectory(path); return path; }
    private PrismInstallationFacts Installation => new(Path.Combine(root, "prism.exe"), root, Path.Combine(root, "instances"), "test", "hash", Key);
    private static ImportInput Input => MakeInput();
    private static ImportInput MakeInput()
    {
        var d = new ImportInputDefinition(1, "modsync.prism-import-input-definition", "test", PackId, "verification", "日本語 空白", "1", Components, [new("overrides/modsync/probe.txt", "probe.txt")]);
        return new(d, JsonSerializer.SerializeToUtf8Bytes(d, ImportJson.Options), new Dictionary<string, byte[]> { ["probe.txt"] = Encoding.UTF8.GetBytes("検証\n") });
    }
    private async Task<ImportAttemptPlan> CreateInstance(string name = "新規", Guid? operationId = null)
    {
        var built = await VerificationPackBuilder.BuildAsync(Input, operationId ?? Guid.NewGuid());
        var path = DirectoryAt("instances/" + name);
        await File.WriteAllTextAsync(Path.Combine(path, "instance.cfg"), "[General]\nname=既存と同名\nInstanceType=OneSix\n");
        await File.WriteAllTextAsync(Path.Combine(path, "mmc-pack.json"), JsonSerializer.Serialize(new { formatVersion = 1, components = Components }, ImportJson.Options));
        using var zip = new ZipArchive(new MemoryStream(built.Archive!));
        foreach (var entry in zip.Entries)
        {
            var target = entry.FullName == "modrinth.index.json" ? "mrpack/modrinth.index.json" : entry.FullName.Replace("overrides/", "minecraft/", StringComparison.Ordinal);
            var targetPath = Path.Combine(path, target);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var source = entry.Open(); await using var file = File.Create(targetPath);
            await source.CopyToAsync(file);
        }
        return built.Plan!;
    }
    private RegistrationRecord Registration(ImportAttemptPlan plan) => new(1, "modsync.prism-instance-registration", PackId, plan.OperationId,
        "verification", root, Key, "新規", Path.Combine(root, "instances", "新規"), "minecraft", "同名", plan, Components, [], ReadinessLevel.Declared, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("relative")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\path")]
    [InlineData("C:\\path:stream")]
    [InlineData("C:\\CON.txt")]
    [InlineData("C:\\NUL")]
    [InlineData("C:\\COM1")]
    [InlineData("C:\\foo\\..\\bar")]
    [InlineData("C:\\foo.")]
    [InlineData("C:\\foo ")]
    public void UnsafePathsAreRejected(string path) => Assert.Throws<IOException>(() => SafePathResolver.ValidateAbsolute(path));
    [Fact]
    public void ExistingCaseAliasesResolveToTheSameActualPath()
    {
        var path = DirectoryAt("日本語 空白");
        Assert.Equal(SafePathResolver.ResolveExisting(path), SafePathResolver.ResolveExisting(path.ToUpperInvariant()));
        Assert.Throws<IOException>(() => SafePathResolver.ResolveChild(root, "../outside"));
    }
    [Fact]
    public async Task JunctionIsRejectedWithoutReadingItsTarget()
    {
        var target = DirectoryAt("target");
        var link = Path.Combine(root, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<IOException>(() => SafePathResolver.ResolveExisting(link)); }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task ObservationIncludesEmptyHiddenAndStagingEntriesWithoutChangingExistingFiles()
    {
        await CreateInstance("既存");
        DirectoryAt("instances/空"); DirectoryAt("instances/.hidden"); DirectoryAt("instances/.tmp/work");
        await File.WriteAllTextAsync(Path.Combine(root, "instances", "file.txt"), "abc");
        var cfg = Path.Combine(root, "instances", "既存", "instance.cfg");
        var original = await File.ReadAllBytesAsync(cfg);
        // 認証ファイルは排他ロックし、観測で開けないようにする。
        var accounts = Path.Combine(root, "accounts.json"); await File.WriteAllTextAsync(accounts, "試験用・実認証情報なし");
        using var locked = new FileStream(accounts, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var gateway = new PrismImportGateway(Installation, new FakeLauncher());
        var before = await gateway.CaptureSnapshotAsync(default);
        Assert.Contains(before.Entries, e => e.Name == "空" && !e.HasInstanceCfg);
        Assert.Contains(before.Entries, e => e.Name == ".hidden");
        Assert.Contains(before.Entries, e => e.Name == "file.txt" && e.Kind == "file");
        Assert.Equal(new[] { "work" }, before.StagingKeys);
        var plan = await CreateInstance();
        var facts = await gateway.ObserveCandidatesAsync(before, default);
        Assert.Equal(ImportAttemptState.Verified, CandidateEvaluator.Evaluate(plan, before, facts).State);
        Assert.Equal(original, await File.ReadAllBytesAsync(cfg));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("schema")]
    [InlineData("operation")]
    public async Task InvalidIdentityCannotBeReplacedByRetainedIndex(string kind)
    {
        DirectoryAt("instances");
        var before = await new PrismImportGateway(Installation).CaptureSnapshotAsync(default);
        var plan = await CreateInstance();
        var path = Path.Combine(root, "instances", "新規", "minecraft", "modsync", "identity.json");
        if (kind == "missing") File.Delete(path);
        else if (kind == "invalid") await File.WriteAllTextAsync(path, "{broken");
        else
        {
            var text = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, kind == "schema" ? text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99") : text.Replace(plan.OperationId.ToString(), Guid.NewGuid().ToString()));
        }
        var facts = await new PrismImportGateway(Installation).ObserveCandidatesAsync(before, default);
        Assert.Equal(ImportAttemptState.Unobservable, CandidateEvaluator.Evaluate(plan, before, facts).State);
    }
    [Fact]
    public async Task BothGameRootsAndMissingNeoForgeAreMismatches()
    {
        var plan = await CreateInstance();
        var path = Path.Combine(root, "instances", "新規");
        Directory.CreateDirectory(Path.Combine(path, ".minecraft"));
        var facts = await new PrismInstanceReader().ReadAsync(Installation.InstancesRoot, "新規", default);
        var before = new InstancesSnapshot(DateTimeOffset.UnixEpoch, [], []);
        Assert.Equal(ImportAttemptState.Mismatch, CandidateEvaluator.Evaluate(plan, before, [facts]).State);
        Directory.Delete(Path.Combine(path, ".minecraft"));
        await File.WriteAllTextAsync(Path.Combine(path, "mmc-pack.json"), "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}]}");
        facts = await new PrismInstanceReader().ReadAsync(Installation.InstancesRoot, "新規", default);
        Assert.Equal(ImportAttemptState.Mismatch, CandidateEvaluator.Evaluate(plan, before, [facts]).State);
    }
    [Fact]
    public async Task ArgumentsKeepJapaneseAndSpacePathsAsSingleValuesWithoutStartingPrism()
    {
        DirectoryAt("instances"); await File.WriteAllTextAsync(Path.Combine(root, "prism.exe"), "模擬実行ファイル");
        var archive = Path.Combine(root, "日本語 空白.mrpack"); await File.WriteAllTextAsync(archive, "模擬アーカイブ");
        var launcher = new FakeLauncher();
        await new PrismImportGateway(Installation, launcher).RequestImportAsync(archive, default);
        Assert.Equal(new[] { "--dir", SafePathResolver.ResolveExisting(root), "--import", SafePathResolver.ResolveExisting(archive) }, launcher.Start!.ArgumentList);
        Assert.False(launcher.Start.UseShellExecute); Assert.False(launcher.Start.RedirectStandardOutput);
        Assert.Equal("", launcher.Start.Arguments);
    }
    [Fact]
    public async Task ProductGatewayAndStoreVerifyRegisterAndReloadWithoutARealPrismProcess()
    {
        var data = DirectoryAt("prism-data");
        var instances = SafePathResolver.ResolveExisting(DirectoryAt("prism-data/instances"));
        var existing = DirectoryAt("prism-data/instances/既存");
        DirectoryAt("prism-data/instances/空");
        var cfg = Path.Combine(existing, "instance.cfg"); await File.WriteAllTextAsync(cfg, "name=既存\n");
        var original = await File.ReadAllBytesAsync(cfg);
        var binary = Path.Combine(data, "prism.exe"); await File.WriteAllTextAsync(binary, "合成実行ファイル・起動禁止");
        var installation = new PrismInstallationFacts(binary, data, instances, "test", "hash", Key);
        var launcher = new FakeLauncher(async start =>
        {
            var path = Path.Combine(instances, "新規"); Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "instance.cfg"), "name=既存\n");
            await File.WriteAllTextAsync(Path.Combine(path, "mmc-pack.json"), JsonSerializer.Serialize(new { formatVersion = 1, components = Components }, ImportJson.Options));
            using var zip = ZipFile.OpenRead(start.ArgumentList[3]);
            foreach (var entry in zip.Entries)
            {
                var relative = entry.FullName == "modrinth.index.json" ? "mrpack/modrinth.index.json" : entry.FullName.Replace("overrides/", "minecraft/", StringComparison.Ordinal);
                var target = Path.Combine(path, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open(); await using var destination = File.Create(target); await source.CopyToAsync(destination);
            }
        });
        var gateway = new PrismImportGateway(installation, launcher);
        var store = new JsonImportRecordStore(DirectoryAt("application"), prismDataRoot: data);
        var options = new ImportWaitOptions(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var coordinator = new InstanceImportCoordinator(gateway, store, waitOptions: options);
        var result = await coordinator.StartAsync(Input);
        Assert.True(result.State == ImportAttemptState.Verified, result.State + " / " + result.Reason);
        Assert.Null(await store.LoadRegistrationAsync(Key, PackId, default));
        Assert.Equal(ImportAttemptState.Registered, (await coordinator.RegisterAsync(result.OperationId)).State);
        var reloaded = new InstanceImportCoordinator(gateway, new JsonImportRecordStore(Path.Combine(root, "application"), prismDataRoot: data), waitOptions: options);
        Assert.Equal(ImportAttemptState.AlreadyRegistered, (await reloaded.RegisterAsync(result.OperationId)).State);
        Assert.Equal(original, await File.ReadAllBytesAsync(cfg));
        Assert.Equal("新規", (await store.LoadRegistrationAsync(Key, PackId, default))!.InstanceId);
    }
    [Fact]
    public async Task LedgerIsReadBackAndNeverReassigned()
    {
        var plan = await CreateInstance(); var store = new JsonImportRecordStore(DirectoryAt("app")); var record = Registration(plan);
        Assert.True((await store.CommitRegistrationAsync(record, default)).Success);
        var saved = await store.LoadRegistrationAsync(Key, PackId, default);
        Assert.Equal(record.InstancePath, saved!.InstancePath);
        Assert.True((await store.CommitRegistrationAsync(record, default)).AlreadyRegistered);
        Assert.False((await store.CommitRegistrationAsync(record with { InstancePath = "C:\\別", OperationId = Guid.NewGuid() }, default)).Success);
        Assert.Equal(record.OperationId, (await store.LoadRegistrationAsync(Key, PackId, default))!.OperationId);
    }
    [Theory]
    [InlineData("beforeWrite")]
    [InlineData("beforeMove")]
    public async Task FailedSaveLeavesNoLedgerAndCleansItsTemporaryFile(string stage)
    {
        var plan = await CreateInstance(); var app = DirectoryAt("app");
        var store = new JsonImportRecordStore(app, s => { if (s == stage) throw new IOException("模擬保存失敗"); });
        Assert.False((await store.CommitRegistrationAsync(Registration(plan), default)).Success);
        Assert.Null(await store.LoadRegistrationAsync(Key, PackId, default));
        Assert.Empty(Directory.EnumerateFiles(app, "*.tmp-*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task ReadBackMismatchKeepsEvidenceAndDoesNotSucceed()
    {
        var plan = await CreateInstance(); var app = DirectoryAt("app");
        var store = new JsonImportRecordStore(app, s =>
        {
            if (s != "beforeRegistrationReadBack") return;
            var path = Path.Combine(app, "prism-import", Key, "registrations", PackId + ".json");
            var saved = JsonSerializer.Deserialize<RegistrationRecord>(File.ReadAllText(path), ImportJson.Options)!;
            File.WriteAllText(path, JsonSerializer.Serialize(saved with { DisplayNameAtRegistration = "変更" }, ImportJson.Options));
        });
        var result = await store.CommitRegistrationAsync(Registration(plan), default);
        Assert.False(result.Success); Assert.Equal("readBackMismatch", result.Reason);
        Assert.NotNull(await store.LoadRegistrationAsync(Key, PackId, default));
    }
    [Fact]
    public async Task OperationRoundTripRejectsUnknownSchemaAndEnum()
    {
        var app = DirectoryAt("app"); var store = new JsonImportRecordStore(app); var id = Guid.NewGuid();
        var record = new ImportOperationRecord { OperationId = id, PackId = PackId, Prism = Installation, State = ImportAttemptState.Writing };
        await store.SaveOperationAsync(record, default);
        Assert.Equal(id, (await store.LoadOperationAsync(Key, id, default))!.OperationId);
        var path = Path.Combine(app, "prism-import", Key, "operations", id.ToString(), "operation.json");
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));
        await Assert.ThrowsAsync<IOException>(() => store.LoadOperationAsync(Key, id, default));
        await File.WriteAllTextAsync(path, original.Replace("\"Writing\"", "\"Unknown\""));
        await Assert.ThrowsAsync<JsonException>(() => store.LoadOperationAsync(Key, id, default));
    }
    [Fact]
    public async Task LookingUpAnUnknownOperationDoesNotCreateAnUnfinishedDirectory()
    {
        var app = DirectoryAt("app"); var store = new JsonImportRecordStore(app); var id = Guid.NewGuid();
        Assert.Null(await store.LoadOperationAsync(Key, id, default));
        Assert.False(Directory.Exists(Path.Combine(app, "prism-import", Key, "operations", id.ToString())));
        Assert.Empty(await store.LoadOperationsAsync(Key, default));
    }
    [Fact]
    public async Task LockExcludesOtherHandlesAndStaleLockRequiresRecovery()
    {
        var app = DirectoryAt("app"); var store = new JsonImportRecordStore(app);
        var first = await store.TryAcquireLockAsync(Key, Guid.NewGuid(), false, default);
        Assert.NotNull(first.Handle);
        Assert.Null((await store.TryAcquireLockAsync(Key, Guid.NewGuid(), false, default)).Handle);
        await first.Handle!.DisposeAsync();
        var path = Path.Combine(app, "prism-import", Key, "import.lock"); await File.WriteAllTextAsync(path, "前回中断");
        Assert.True((await store.TryAcquireLockAsync(Key, Guid.NewGuid(), false, default)).Stale);
        var recovery = await store.TryAcquireLockAsync(Key, Guid.NewGuid(), true, default);
        Assert.NotNull(recovery.Handle); await recovery.Handle!.DisposeAsync();
    }
    [Fact]
    public async Task RecordsCannotBeStoredInsideThePrismDataRoot()
    {
        var app = Path.Combine(root, "unsafe-app");
        var store = new JsonImportRecordStore(app, prismDataRoot: root);
        await Assert.ThrowsAsync<IOException>(() => store.TryAcquireLockAsync(Key, Guid.NewGuid(), false, default));
        Assert.False(Directory.Exists(app));
    }
    [Fact]
    public async Task ProtectionSnapshotIncludesEmptyDirectoriesExcludesLockedCredentialsAndDetectsChanges()
    {
        var instances = DirectoryAt("instances"); DirectoryAt("instances/既存/空");
        var normal = Path.Combine(root, "instances", "既存", "instance.cfg"); await File.WriteAllTextAsync(normal, "original");
        var account = Path.Combine(root, "instances", "既存", "accounts.json"); await File.WriteAllTextAsync(account, "合成入力");
        using var locked = new FileStream(account, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var snapshotter = new InstanceTreeSnapshotter();
        var before = await snapshotter.CaptureAsync(instances, ["既存"], "beforeImport");
        Assert.Contains(before.Entries, e => e.Path == "既存/空" && e.Kind == "directory");
        Assert.DoesNotContain(before.Entries, e => e.Path.EndsWith("accounts.json", StringComparison.Ordinal));
        Assert.True(InstanceTreeSnapshotter.Compare(before, await snapshotter.CaptureAsync(instances, ["既存"], "afterImport")).Unchanged);
        await File.WriteAllTextAsync(normal, "changed");
        var after = await snapshotter.CaptureAsync(instances, ["既存"], "afterReadinessCheck");
        Assert.False(InstanceTreeSnapshotter.Compare(before, after).Unchanged);
    }
    [Fact]
    public async Task MeasurementPreservesBaselineStagesAndCopiesEvidenceToTheOperation()
    {
        var instances = DirectoryAt("instances"); DirectoryAt("instances/既存/空");
        var app = DirectoryAt("app"); var evidence = Path.Combine(app, "measurement.json");
        var snapshotter = new InstanceTreeSnapshotter();
        var baseline = await snapshotter.CaptureAsync(instances, ["既存"], "beforeImport");
        await InstanceTreeSnapshotter.AppendEvidenceAsync(evidence, baseline, "prismAlreadyRunning", true);
        var result = await InstanceTreeSnapshotter.AppendEvidenceAsync(evidence,
            await snapshotter.CaptureAsync(instances, ["既存"], "afterImport"), "prismAlreadyRunning", true);
        Assert.Equal("unchangedInObservedStages", result.OverallResult);
        Assert.Single(result.UnconfirmedRanges); Assert.Equal(2, result.Snapshots.Count);
        var id = Guid.NewGuid(); var operation = DirectoryAt("app/operations/" + id);
        await File.WriteAllTextAsync(Path.Combine(operation, "operation.json"), "合成記録・判定用ではない");
        await InstanceTreeSnapshotter.AttachOperationAsync(evidence, id, "test");
        var document = JsonSerializer.Deserialize<PrismImportMeasurement>(await File.ReadAllTextAsync(Path.Combine(operation, "measurement.json")), ImportJson.Options)!;
        Assert.Contains(id, document.OperationIds);
        Assert.Equal("prismAlreadyRunning", document.ProtectedComparison.BaselineKind);
        Assert.Equal(6, document.Conditions.Count);
        Assert.All(document.Conditions, condition => Assert.Equal("unconfirmed", condition["result"].ToString()));
    }
    [Fact]
    public async Task ReadinessWithDeclarationOnlyDoesNotClaimLibrariesArePrepared()
    {
        await CreateInstance(); DirectoryAt("libraries");
        var result = await new PrismReadinessProbe(root).ReadAsync(Path.Combine(root, "instances", "新規"), Components, default);
        Assert.Equal(ReadinessLevel.Declared, ReadinessEvaluator.Evaluate(result, DateTimeOffset.UnixEpoch).Level);
        Assert.NotEmpty(result.Missing);
    }
    [Theory]
    [InlineData("metadataOnly", ReadinessLevel.MetadataResolved)]
    [InlineData("badMetadataHash", ReadinessLevel.Mismatch)]
    [InlineData("badLibraryHash", ReadinessLevel.Mismatch)]
    [InlineData("noHash", ReadinessLevel.Unconfirmed)]
    [InlineData("unknownRules", ReadinessLevel.Unconfirmed)]
    [InlineData("ready", ReadinessLevel.Ready)]
    [InlineData("loaderMissing", ReadinessLevel.FilesPresent)]
    [InlineData("loaderReady", ReadinessLevel.Ready)]
    public async Task OfficialMetadataAndArtifactHashesDetermineReadiness(string scenario, ReadinessLevel expected)
    {
        await CreateInstance();
        var jar = Encoding.UTF8.GetBytes("合成した本体JAR");
        var sha1 = await Sha1(jar);
        var mainDownload = scenario == "noHash" ? (object)new { size = jar.Length } : new { size = jar.Length, sha1 = scenario == "badLibraryHash" ? new string('0', 40) : sha1 };
        object main = scenario == "unknownRules" ? new { name = "com.mojang:minecraft:1.21.1:client", downloads = new { artifact = mainDownload }, rules = new[] { new { action = "allow", features = new { unknown = true } } } } :
            new { name = "com.mojang:minecraft:1.21.1:client", downloads = new { artifact = mainDownload } };
        var minecraft = JsonSerializer.SerializeToUtf8Bytes(new { uid = "net.minecraft", version = "1.21.1", mainJar = main }, ImportJson.Options);
        byte[] neoforge;
        if (scenario.StartsWith("loader", StringComparison.Ordinal))
        {
            var output = Encoding.UTF8.GetBytes("合成したローダー生成物");
            using var installer = new MemoryStream();
            using (var zip = new ZipArchive(installer, ZipArchiveMode.Create, true))
            {
                await using var stream = zip.CreateEntry("install_profile.json").Open();
                await JsonSerializer.SerializeAsync(stream, new { data = new Dictionary<string, object> {
                    ["PATCHED"] = new { client = "[net.neoforged:neoforge:21.1.228:client]" }, ["PATCHED_SHA"] = new { client = await Sha1(output) } } });
            }
            var bytes = installer.ToArray();
            var relative = PrismReadinessProbe.MavenPath("net.neoforged:neoforge:21.1.228:installer");
            var installerPath = Path.Combine(root, "libraries", relative); Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            await File.WriteAllBytesAsync(installerPath, bytes);
            neoforge = JsonSerializer.SerializeToUtf8Bytes(new { uid = "net.neoforged", version = "21.1.228", mavenFiles = new[] {
                new { name = "net.neoforged:neoforge:21.1.228:installer", downloads = new { artifact = new { size = bytes.Length, sha1 = await Sha1(bytes) } } } } });
            if (scenario == "loaderReady")
            {
                var generated = Path.Combine(root, "libraries", PrismReadinessProbe.MavenPath("net.neoforged:neoforge:21.1.228:client"));
                await File.WriteAllBytesAsync(generated, output);
            }
        }
        else neoforge = JsonSerializer.SerializeToUtf8Bytes(new { uid = "net.neoforged", version = "21.1.228" });
        foreach (var (component, bytes) in new[] { (Components[0], minecraft), (Components[1], neoforge) })
        {
            var directory = DirectoryAt("meta/" + component.Uid);
            await File.WriteAllBytesAsync(Path.Combine(directory, component.Version + ".json"), bytes);
            using var stream = new MemoryStream(bytes);
            var sha256 = scenario == "badMetadataHash" ? new string('0', 64) : await ContentHash.ComputeSha256Async(stream);
            await File.WriteAllTextAsync(Path.Combine(directory, "index.json"), JsonSerializer.Serialize(new { versions = new[] { new { version = component.Version, sha256 } } }));
        }
        if (scenario != "metadataOnly")
        {
            var path = Path.Combine(root, "libraries", PrismReadinessProbe.MavenPath("com.mojang:minecraft:1.21.1:client"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, jar);
        }
        var result = await new PrismReadinessProbe(root).ReadAsync(Path.Combine(root, "instances", "新規"), Components, default);
        Assert.Equal(expected, ReadinessEvaluator.Evaluate(result, DateTimeOffset.UnixEpoch).Level);
    }
    private static async Task<string> Sha1(byte[] bytes)
    { using var stream = new MemoryStream(bytes); return await ContentHash.ComputeSha1Async(stream); }
    [Theory]
    [InlineData("../evil:thing:1")]
    [InlineData("group:../evil:1")]
    [InlineData("group:thing:..")]
    [InlineData("group:thing:1@../exe")]
    public void MetadataTraversalIsRejected(string coordinate) => Assert.Throws<IOException>(() => PrismReadinessProbe.MavenPath(coordinate));
    private sealed class FakeLauncher(Func<ProcessStartInfo, Task>? place = null) : IPrismProcessLauncher
    {
        public ProcessStartInfo? Start { get; private set; }
        public async Task<ImportRequestResult> LaunchAsync(ProcessStartInfo start, CancellationToken ct)
        { Start = start; if (place != null) await place(start); return new(DateTimeOffset.UnixEpoch, "runningInstance", 0, DateTimeOffset.UnixEpoch); }
    }
}
