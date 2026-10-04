using System.Text;
using System.Text.Json;
using ModSync.Core.InstanceSetup;
using ModSync.Core.Integrity;
using ModSync.Platform.Prism;

namespace ModSync.Platform.FileSystem;

public sealed class JsonImportRecordStore(string applicationRoot, Action<string>? saveHook = null, string? prismDataRoot = null) : IImportRecordStore
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinecraftModSync");
    private string Root(string key)
    {
        if (key.Length != 16 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("データルートキーが不正です。");
        SafePathResolver.ValidateAbsolute(applicationRoot);
        if (prismDataRoot != null)
        {
            var prism = SafePathResolver.ResolveExisting(prismDataRoot);
            var app = Path.GetFullPath(applicationRoot).TrimEnd('\\');
            if (StringComparer.OrdinalIgnoreCase.Equals(app, prism) || app.StartsWith(prism + "\\", StringComparison.OrdinalIgnoreCase) ||
                prism.StartsWith(app + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("アプリ記録領域はPrismデータ領域と分離してください。");
        }
        CreateSafeDirectory(applicationRoot);
        var root = Path.Combine(applicationRoot, "prism-import", key);
        CreateSafeDirectory(root);
        return SafePathResolver.ResolveExisting(root);
    }
    private string OperationDirectory(string key, Guid id)
    {
        if (id == Guid.Empty) throw new IOException("処理IDが不正です。");
        var path = Path.Combine(Root(key), "operations", id.ToString());
        CreateSafeDirectory(path);
        return SafePathResolver.ResolveExisting(path);
    }
    private static void CreateSafeDirectory(string path)
    {
        if (Directory.Exists(path)) { SafePathResolver.ResolveExisting(path); return; }
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("親がありません。");
        CreateSafeDirectory(parent);
        Directory.CreateDirectory(path);
        SafePathResolver.ResolveExisting(path);
    }
    private static string PackName(string packId)
    {
        if (!Guid.TryParseExact(packId, "D", out var id) || id.ToString() != packId) throw new IOException("packIdが不正です。");
        return packId + ".json";
    }
    public Task<LockResult> TryAcquireLockAsync(string key, Guid operationId, bool recovery, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(Root(key), "import.lock");
        var stale = File.Exists(path);
        if (stale) SafePathResolver.ResolveExisting(path);
        try
        {
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (stale && !recovery) { file.Dispose(); return Task.FromResult(new LockResult(null, true)); }
            file.SetLength(0);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { operationId, processId = Environment.ProcessId, startedAt = DateTimeOffset.UtcNow });
            file.Write(bytes);
            file.Flush(true);
            return Task.FromResult(new LockResult(new ImportLock(file, path), stale));
        }
        catch (IOException) { return Task.FromResult(new LockResult(null, stale)); }
    }
    private sealed class ImportLock(FileStream file, string path) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await file.DisposeAsync();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public Task SaveOperationAsync(ImportOperationRecord record, CancellationToken ct) => WriteAtomic(
        Path.Combine(OperationDirectory(record.Prism.DataRootKey, record.OperationId), "operation.json"),
        JsonSerializer.SerializeToUtf8Bytes(record, ImportJson.Options), true, ct);
    public async Task<ImportOperationRecord?> LoadOperationAsync(string key, Guid operationId, CancellationToken ct)
    {
        var path = Path.Combine(OperationDirectory(key, operationId), "operation.json");
        if (!File.Exists(path)) return null;
        var record = await Read<ImportOperationRecord>(path, ct);
        if (record.SchemaVersion != 1 || record.Kind != "modsync.prism-import-operation" || record.OperationId != operationId ||
            record.Prism.DataRootKey != key || record.Terminal != ImportAttemptStateMachine.IsTerminal(record.State)) throw new IOException("処理記録の形式・状態が不正です。");
        return record;
    }
    public async Task<ImportOperationRecord[]> LoadOperationsAsync(string key, CancellationToken ct)
    {
        var root = Path.Combine(Root(key), "operations");
        if (!Directory.Exists(root)) return [];
        SafePathResolver.ResolveExisting(root);
        var records = new List<ImportOperationRecord>();
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            SafePathResolver.ResolveExisting(path);
            if (!Guid.TryParseExact(Path.GetFileName(path), "D", out var id)) throw new IOException("未知の処理フォルダーです。");
            var record = await LoadOperationAsync(key, id, ct);
            if (record == null) throw new IOException("処理記録が欠損しています。");
            records.Add(record);
        }
        return records.ToArray();
    }
    public async Task<string> SaveArchiveAsync(string key, ImportAttemptPlan plan, byte[] archive, CancellationToken ct)
    {
        SafePathResolver.ValidateSegment(plan.ArchiveFileName);
        using var source = new MemoryStream(archive);
        if (await ContentHash.ComputeSha256Async(source, ct) != plan.ArchiveSha256) throw new IOException("アーカイブのハッシュが一致しません。");
        var directory = Path.Combine(OperationDirectory(key, plan.OperationId), "input");
        CreateSafeDirectory(directory);
        var path = Path.Combine(directory, plan.ArchiveFileName);
        await WriteAtomic(path, archive, false, ct);
        return SafePathResolver.ResolveExisting(path);
    }
    public async Task<RegistrationRecord?> LoadRegistrationAsync(string key, string packId, CancellationToken ct)
    {
        var path = Path.Combine(Root(key), "registrations", PackName(packId));
        if (!File.Exists(path)) return null;
        var registration = await Read<RegistrationRecord>(path, ct);
        if (registration.SchemaVersion != 1 || registration.Kind != "modsync.prism-instance-registration" || registration.PackId != packId ||
            registration.DataRootKey != key || registration.OperationId == Guid.Empty) throw new IOException("登録台帳の形式が不正です。");
        return registration;
    }
    public async Task<CommitResult> CommitRegistrationAsync(RegistrationRecord record, CancellationToken ct)
    {
        var existing = await LoadRegistrationAsync(record.DataRootKey, record.PackId, ct);
        if (existing != null)
        {
            var same = SameRegistration(existing, record);
            return new(same, same, same ? "alreadyRegistered" : "conflictingRegistration");
        }
        var directory = Path.Combine(Root(record.DataRootKey), "registrations");
        CreateSafeDirectory(directory);
        var path = Path.Combine(directory, PackName(record.PackId));
        try
        {
            await WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(record, ImportJson.Options), false, ct);
            saveHook?.Invoke("beforeRegistrationReadBack");
            var readBack = await LoadRegistrationAsync(record.DataRootKey, record.PackId, ct);
            if (readBack == null || JsonSerializer.Serialize(readBack, ImportJson.Options) != JsonSerializer.Serialize(record, ImportJson.Options))
                return new(false, false, "readBackMismatch");
            return new(true, false, "readBackMatched");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(false, false, "writeFailed: " + e.Message); }
    }
    private static bool SameRegistration(RegistrationRecord a, RegistrationRecord b) => a.PackId == b.PackId && a.OperationId == b.OperationId &&
        StringComparer.OrdinalIgnoreCase.Equals(a.DataRoot, b.DataRoot) && StringComparer.OrdinalIgnoreCase.Equals(a.InstancePath, b.InstancePath) &&
        StringComparer.OrdinalIgnoreCase.Equals(a.InstanceId, b.InstanceId) && a.GameRoot == b.GameRoot &&
        a.Input.ArchiveSha256 == b.Input.ArchiveSha256 && a.Input.TemplateSha256 == b.Input.TemplateSha256 &&
        a.Input.IndexSha256 == b.Input.IndexSha256 && a.Input.IdentitySha256 == b.Input.IdentitySha256 && a.Input.ProbeSha256 == b.Input.ProbeSha256 &&
        a.VerifiedFiles.SequenceEqual(b.VerifiedFiles) && a.VerifiedComponents.SequenceEqual(b.VerifiedComponents);
    private static async Task<T> Read<T>(string path, CancellationToken ct)
    {
        await using var stream = PrismInstallation.SharedRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, ImportJson.Options, ct) ?? throw new IOException("JSONが空です。");
    }
    private async Task WriteAtomic(string path, byte[] bytes, bool overwrite, CancellationToken ct)
    {
        SafePathResolver.ResolveExisting(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) SafePathResolver.ResolveExisting(path);
        var temporary = path + ".tmp-" + Guid.NewGuid();
        try
        {
            saveHook?.Invoke("beforeWrite");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(true);
            }
            saveHook?.Invoke("beforeMove");
            File.Move(temporary, path, overwrite);
            if (overwrite)
            {
                var saved = await File.ReadAllBytesAsync(SafePathResolver.ResolveExisting(path), ct);
                if (!saved.SequenceEqual(bytes)) throw new IOException("保存後の読み戻しが一致しません。");
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
