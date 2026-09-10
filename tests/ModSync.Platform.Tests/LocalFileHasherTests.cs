using System.Text;
using ModSync.Platform.FileSystem;

namespace ModSync.Platform.Tests;

public sealed class LocalFileHasherTests
{
    [Fact]
    public async Task HashesAFileWithoutChangingItAndReleasesItsHandle()
    {
        var path = Path.GetTempFileName();
        try
        {
            var original = Encoding.UTF8.GetBytes("abc");
            await File.WriteAllBytesAsync(path, original);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                await new LocalFileHasher().ComputeSha256Async(path));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFilesAreReportedInsteadOfCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), "modsync-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new LocalFileHasher().ComputeSha256Async(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FilesLockedForWritingAreReported()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAsync<IOException>(() => new LocalFileHasher().ComputeSha256Async(path));
        }
        finally { File.Delete(path); }
    }
}
