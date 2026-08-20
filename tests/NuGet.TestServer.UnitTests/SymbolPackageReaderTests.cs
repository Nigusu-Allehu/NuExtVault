using NuGet.TestServer.Kernel.Owners;

namespace NuGet.TestServer.UnitTests;

public sealed class SymbolPackageReaderTests
{
    [Fact]
    public async Task Known_length_content_is_read_into_one_exact_buffer()
    {
        var content = CreateContent(4096);
        using var source = new MemoryStream(content, writable: false);

        var result = await SymbolPackageReader.ReadAsync(
            source,
            content.Length,
            long.MaxValue,
            CancellationToken.None);

        Assert.Equal(content, result);
        Assert.Equal(content.Length, result.Length);
    }

    [Fact]
    public async Task Unknown_length_content_is_read_completely()
    {
        var content = CreateContent(9000);
        using var source = new MemoryStream(content, writable: false);

        var result = await SymbolPackageReader.ReadAsync(source, 0, long.MaxValue, CancellationToken.None);

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task Content_shorter_than_the_declared_length_is_not_padded()
    {
        var content = CreateContent(128);
        using var source = new MemoryStream(content, writable: false);

        var result = await SymbolPackageReader.ReadAsync(source, 512, long.MaxValue, CancellationToken.None);

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task Content_longer_than_the_declared_length_is_not_truncated()
    {
        var content = CreateContent(1024);
        using var source = new MemoryStream(content, writable: false);

        var result = await SymbolPackageReader.ReadAsync(source, 512, long.MaxValue, CancellationToken.None);

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task Reads_observe_cancellation()
    {
        using var source = new MemoryStream(CreateContent(64), writable: false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SymbolPackageReader.ReadAsync(source, 64, long.MaxValue, cancellation.Token));
    }

    [Fact]
    public async Task Declared_lengths_beyond_the_configured_limit_do_not_size_the_buffer()
    {
        var content = CreateContent(2048);
        using var source = new MemoryStream(content, writable: false);

        var result = await SymbolPackageReader.ReadAsync(
            source,
            long.MaxValue,
            1024,
            CancellationToken.None);

        Assert.Equal(content, result);
    }

    private static byte[] CreateContent(int length)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        return content;
    }
}
