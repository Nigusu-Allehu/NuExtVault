using System.Buffers;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed record TestPackage : IDisposable
{
    private byte[]? MemoryContent { get; init; }
    private string? ContentPath { get; init; }
    private bool OwnsContentPath { get; init; }
    private long StoredContentLength { get; init; }

    public required PackageIdentity Identity { get; init; }
    public byte[] Content
    {
        get => MemoryContent ?? File.ReadAllBytes(ContentPath!);
        init
        {
            MemoryContent = value ?? throw new ArgumentNullException(nameof(value));
            StoredContentLength = value.LongLength;
        }
    }

    public long ContentLength => StoredContentLength;
    public required byte[] NuspecContent { get; init; }
    public required string NormalizedVersion { get; init; }
    public required string Description { get; init; }
    public required string Authors { get; init; }
    public required string Tags { get; init; }
    public required IReadOnlyList<PackageDependencyGroup> DependencyGroups { get; init; }
    public required DateTimeOffset Published { get; init; }
    public bool IsListed { get; init; } = true;

    public static TestPackage FromContent(byte[] content, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var limits = PackageTransferLimits.Default;
        EnsurePackageSize(content.LongLength, limits);

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            return Parse(
                stream,
                content.LongLength,
                limits,
                timeProvider,
                memoryContent: content.ToArray(),
                contentPath: null,
                ownsContentPath: false);
        }
        catch (Exception exception) when (IsInvalidPackageException(exception))
        {
            throw new InvalidPackageException("The content is not a valid NuGet package.", exception);
        }
    }

    public static async ValueTask<TestPackage> FromStreamAsync(
        Stream content,
        PackageTransferLimits? limits = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        limits = (limits ?? PackageTransferLimits.Default).Validate();
        Directory.CreateDirectory(limits.TemporaryDirectory);
        var temporaryPath = Path.Combine(
            limits.TemporaryDirectory,
            $"{Guid.NewGuid():N}.nupkg.tmp");

        try
        {
            var length = await CopyPackageAsync(
                content,
                temporaryPath,
                limits.MaxPackageBytes,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using var packageStream = OpenFile(temporaryPath);
            return Parse(
                packageStream,
                length,
                limits,
                timeProvider,
                memoryContent: null,
                contentPath: temporaryPath,
                ownsContentPath: true);
        }
        catch (Exception exception) when (IsInvalidPackageException(exception))
        {
            DeleteTemporaryFile(temporaryPath);
            throw new InvalidPackageException("The content is not a valid NuGet package.", exception);
        }
        catch
        {
            DeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static TestPackage FromFile(
        string path,
        PackageTransferLimits limits,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        limits = limits.Validate();
        var length = new FileInfo(path).Length;
        EnsurePackageSize(length, limits);

        try
        {
            using var stream = OpenFile(path);
            return Parse(
                stream,
                length,
                limits,
                timeProvider,
                memoryContent: null,
                contentPath: Path.GetFullPath(path),
                ownsContentPath: false);
        }
        catch (Exception exception) when (IsInvalidPackageException(exception))
        {
            throw new InvalidPackageException("The content is not a valid NuGet package.", exception);
        }
    }

    internal static TestPackage FromMetadata(
        PackageMetadata metadata,
        string contentPath)
    {
        using var nuspecStream = new MemoryStream(metadata.Nuspec, writable: false);
        var nuspec = new NuspecReader(nuspecStream);
        return new TestPackage
        {
            Identity = new PackageIdentity(
                metadata.Id,
                NuGetVersion.Parse(metadata.OriginalVersion)),
            ContentPath = Path.GetFullPath(contentPath),
            StoredContentLength = metadata.ContentLength,
            NuspecContent = metadata.Nuspec,
            NormalizedVersion = metadata.NormalizedVersion,
            Description = metadata.Description,
            Authors = metadata.Authors,
            Tags = metadata.Tags,
            DependencyGroups = nuspec.GetDependencyGroups().ToArray(),
            Published = metadata.Published,
            IsListed = metadata.IsListed
        };
    }

    public Stream OpenReadStream()
    {
        if (MemoryContent is not null)
        {
            return new MemoryStream(MemoryContent, writable: false);
        }

        return OpenFile(ContentPath!);
    }

    internal TestPackage WithContentFile(string path, bool ownsPath) => this with
    {
        MemoryContent = null,
        ContentPath = Path.GetFullPath(path),
        OwnsContentPath = ownsPath
    };

    public void Dispose()
    {
        if (OwnsContentPath && ContentPath is not null)
        {
            File.Delete(ContentPath);
        }
    }

    public static string NormalizeVersion(NuGetVersion version)
    {
        return version.ToNormalizedString().Split('+', 2)[0].ToLowerInvariant();
    }

    private static TestPackage Parse(
        Stream stream,
        long contentLength,
        PackageTransferLimits limits,
        TimeProvider? timeProvider,
        byte[]? memoryContent,
        string? contentPath,
        bool ownsContentPath)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > limits.MaxArchiveEntries)
        {
            throw new PackageLimitExceededException(
                $"The package archive exceeds the limit of {limits.MaxArchiveEntries} entries.");
        }

        long expandedSize = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > limits.MaxArchiveEntryBytes)
            {
                throw new PackageLimitExceededException(
                    $"Archive entry '{entry.FullName}' exceeds the limit of " +
                    $"{limits.MaxArchiveEntryBytes} bytes.");
            }

            try
            {
                expandedSize = checked(expandedSize + entry.Length);
            }
            catch (OverflowException)
            {
                throw new PackageLimitExceededException(
                    $"The expanded package exceeds the limit of " +
                    $"{limits.MaxExpandedArchiveBytes} bytes.");
            }

            if (expandedSize > limits.MaxExpandedArchiveBytes)
            {
                throw new PackageLimitExceededException(
                    $"The expanded package exceeds the limit of " +
                    $"{limits.MaxExpandedArchiveBytes} bytes.");
            }
        }

        var nuspecEntry = archive.Entries.SingleOrDefault(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The package does not contain a nuspec.");
        using var nuspecStream = nuspecEntry.Open();
        using var nuspecBuffer = new MemoryStream(
            capacity: checked((int)Math.Min(nuspecEntry.Length, int.MaxValue)));
        CopyArchiveEntry(nuspecStream, nuspecBuffer, limits.MaxArchiveEntryBytes);
        if (nuspecBuffer.Length != nuspecEntry.Length)
        {
            throw new InvalidDataException("The nuspec entry has an invalid expanded size.");
        }

        nuspecBuffer.Position = 0;
        var document = XDocument.Load(nuspecBuffer);
        var metadata = document.Root?.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "metadata")
            ?? throw new InvalidDataException("The nuspec has no metadata element.");
        var id = RequiredValue(metadata, "id");
        var version = NuGetVersion.Parse(RequiredValue(metadata, "version"));
        var identity = new PackageIdentity(id, version);
        var dependencies = metadata.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => new PackageDependency(
                element.Attribute("id")?.Value
                    ?? throw new InvalidDataException("A dependency has no ID."),
                VersionRange.Parse(element.Attribute("version")?.Value ?? "(,)")))
            .ToArray();

        return new TestPackage
        {
            Identity = identity,
            MemoryContent = memoryContent,
            ContentPath = contentPath,
            OwnsContentPath = ownsContentPath,
            StoredContentLength = contentLength,
            NuspecContent = nuspecBuffer.ToArray(),
            NormalizedVersion = NormalizeVersion(identity.Version),
            Description = Value(metadata, "description"),
            Authors = Value(metadata, "authors"),
            Tags = Value(metadata, "tags"),
            DependencyGroups = dependencies.Length == 0
                ? []
                : [new PackageDependencyGroup(NuGetFramework.AnyFramework, dependencies)],
            Published = (timeProvider ?? TimeProvider.System).GetUtcNow(),
            IsListed = true
        };
    }

    private static async Task<long> CopyPackageAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        CancellationToken token)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, token);
                if (read == 0)
                {
                    return total;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new PackageLimitExceededException(
                        $"The package size exceeds the limit of {maximumBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyArchiveEntry(
        Stream source,
        Stream destination,
        long maximumBytes)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new PackageLimitExceededException(
                        $"An archive entry exceeds the limit of {maximumBytes} bytes.");
                }

                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream OpenFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void EnsurePackageSize(long length, PackageTransferLimits limits)
    {
        if (length > limits.MaxPackageBytes)
        {
            throw new PackageLimitExceededException(
                $"The package size exceeds the limit of {limits.MaxPackageBytes} bytes.");
        }
    }

    private static bool IsInvalidPackageException(Exception exception) =>
        exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException or
            XmlException;

    private static void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string RequiredValue(XElement metadata, string name)
    {
        var value = Value(metadata, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"The nuspec has no {name}.")
            : value;
    }

    private static string Value(XElement metadata, string name) =>
        metadata.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value
        ?? string.Empty;
}

public sealed class InvalidPackageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class PackageLimitExceededException(string message) : Exception(message);

public sealed class DuplicatePackageException(string id, string version)
    : Exception($"Package '{id} {version}' already exists.");
