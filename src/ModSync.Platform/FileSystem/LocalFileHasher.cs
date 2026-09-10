using ModSync.Core.Integrity;

namespace ModSync.Platform.FileSystem;

/// <summary>Read-only file access for the shared content-hash operation.</summary>
public sealed class LocalFileHasher
{
    public async Task<string> ComputeSha256Async(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ContentHash.ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }
}
