using System.Security.Cryptography;

namespace ModSync.Core.Integrity;

/// <summary>Reads from the current position without taking ownership of the stream.</summary>
public static class ContentHash
{
    public static async Task<string> ComputeSha256Async(
        Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
