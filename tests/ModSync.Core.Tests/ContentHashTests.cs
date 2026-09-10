using System.Text;
using ModSync.Core.Integrity;

namespace ModSync.Core.Tests;

public sealed class ContentHashTests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task ComputesKnownSha256Vectors(string value, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));
        Assert.Equal(expected, await ContentHash.ComputeSha256Async(stream));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadsFromTheCurrentPosition()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("prefixabc"));
        stream.Position = 6;
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await ContentHash.ComputeSha256Async(stream));
    }

    [Fact]
    public async Task AcceptsAStreamWithoutSeekOrLength()
    {
        using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes("abc"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await ContentHash.ComputeSha256Async(stream));
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContentHash.ComputeSha256Async(stream, new CancellationToken(canceled: true)));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
