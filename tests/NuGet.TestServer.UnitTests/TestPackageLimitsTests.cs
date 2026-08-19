using System.IO.Compression;
using System.Text;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class TestPackageLimitsTests
{
    [Fact]
    public async Task Stream_parser_rejects_packages_larger_than_the_configured_limit()
    {
        using var temporary = TemporaryDirectory.Create();
        await using var content = new MemoryStream(CreatePackage(("large.bin", new byte[4096])));
        var limits = Limits(temporary.Path) with { MaxPackageBytes = content.Length - 1 };

        var exception = await Assert.ThrowsAsync<PackageLimitExceededException>(
            () => TestPackage.FromStreamAsync(content, limits).AsTask());

        Assert.Contains("package size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public async Task Stream_parser_rejects_too_many_archive_entries()
    {
        using var temporary = TemporaryDirectory.Create();
        await using var content = new MemoryStream(CreatePackage(
            ("first.txt", "first"u8.ToArray()),
            ("second.txt", "second"u8.ToArray())));
        var limits = Limits(temporary.Path) with { MaxArchiveEntries = 2 };

        var exception = await Assert.ThrowsAsync<PackageLimitExceededException>(
            () => TestPackage.FromStreamAsync(content, limits).AsTask());

        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public async Task Stream_parser_rejects_oversized_archive_entries()
    {
        using var temporary = TemporaryDirectory.Create();
        await using var content = new MemoryStream(CreatePackage(("large.bin", new byte[4096])));
        var limits = Limits(temporary.Path) with { MaxArchiveEntryBytes = 1024 };

        var exception = await Assert.ThrowsAsync<PackageLimitExceededException>(
            () => TestPackage.FromStreamAsync(content, limits).AsTask());

        Assert.Contains("entry", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public async Task Stream_parser_rejects_excessive_expanded_archive_size()
    {
        using var temporary = TemporaryDirectory.Create();
        await using var content = new MemoryStream(CreatePackage(
            ("first.bin", new byte[800]),
            ("second.bin", new byte[800])));
        var limits = Limits(temporary.Path) with
        {
            MaxArchiveEntryBytes = 4096,
            MaxExpandedArchiveBytes = 1400
        };

        var exception = await Assert.ThrowsAsync<PackageLimitExceededException>(
            () => TestPackage.FromStreamAsync(content, limits).AsTask());

        Assert.Contains("expanded", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public async Task Stream_parser_rejects_malformed_packages_and_removes_temporary_files()
    {
        using var temporary = TemporaryDirectory.Create();
        await using var content = new MemoryStream("not a zip archive"u8.ToArray());

        var exception = await Assert.ThrowsAsync<InvalidPackageException>(
            () => TestPackage.FromStreamAsync(content, Limits(temporary.Path)).AsTask());

        Assert.Contains("valid NuGet package", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public async Task Stream_parser_honors_cancellation_and_removes_temporary_files()
    {
        using var temporary = TemporaryDirectory.Create();
        await using var content = new CancelingStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TestPackage.FromStreamAsync(
                content,
                Limits(temporary.Path),
                cancellationToken: cancellation.Token).AsTask());

        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    private static PackageTransferLimits Limits(string temporaryDirectory) => new()
    {
        MaxRequestBodyBytes = 32 * 1024,
        MaxPackageBytes = 16 * 1024,
        MaxArchiveEntries = 10,
        MaxArchiveEntryBytes = 8 * 1024,
        MaxExpandedArchiveBytes = 16 * 1024,
        TemporaryDirectory = temporaryDirectory
    };

    private static byte[] CreatePackage(params (string Path, byte[] Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "Limits.Package.nuspec",
                Encoding.UTF8.GetBytes(
                    """
                    <package>
                      <metadata>
                        <id>Limits.Package</id>
                        <version>1.0.0</version>
                        <authors>tests</authors>
                        <description>limits</description>
                      </metadata>
                    </package>
                    """));
            foreach (var file in files)
            {
                WriteEntry(archive, file.Path, file.Content);
            }
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private sealed class CancelingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TemporaryDirectory Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.UnitTests",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
