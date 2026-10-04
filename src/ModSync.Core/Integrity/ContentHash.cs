using System.Security.Cryptography;

namespace ModSync.Core.Integrity;

/// <summary>Reads from the current position without taking ownership of the stream.</summary>
public static class ContentHash
{
    /// <summary>公開メタデータのSHA-1と照合する。独自の識別には使わない。</summary>
    public static async Task<string> ComputeSha1Async(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<string> ComputeSha256Async(
        Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
