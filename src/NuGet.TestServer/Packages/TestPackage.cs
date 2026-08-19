using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed record TestPackage : IDisposable
{
    private static readonly IReadOnlyList<PackageTypeMetadata> DefaultPackageTypes =
        [new("Dependency", string.Empty)];
    private byte[]? MemoryContent { get; init; }
    private string? ContentPath { get; init; }
    private bool OwnsContentPath { get; init; }
    private long StoredContentLength { get; init; }
    private byte[]? ExpectedContentSha256 { get; init; }

    public required PackageIdentity Identity { get; init; }
    public byte[] Content
    {
        get
        {
            if (MemoryContent is not null)
            {
                return MemoryContent;
            }

            using var stream = OpenReadStream();
            using var buffer = new MemoryStream(capacity: checked((int)StoredContentLength));
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
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
    public required string Summary { get; init; }
    public required string Title { get; init; }
    public required string Authors { get; init; }
    public required string Tags { get; init; }
    public required Uri? ProjectUrl { get; init; }
    public required string Readme { get; init; }
    public required string Icon { get; init; }
    public required string LicenseExpression { get; init; }
    public required string LicenseFile { get; init; }
    public required Uri? LicenseUrl { get; init; }
    public required IReadOnlyList<PackageTypeMetadata> PackageTypes { get; init; }
    public IReadOnlyList<PackageTypeMetadata> EffectivePackageTypes =>
        PackageTypes.Count == 0 ? DefaultPackageTypes : PackageTypes;
    public required RepositoryMetadata? Repository { get; init; }
    public required string PackageHash { get; init; }
    public required PackageRepositoryMetadata RepositoryMetadata { get; init; }
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
        using var documentStream = new MemoryStream(metadata.Nuspec, writable: false);
        var document = XDocument.Load(documentStream);
        using var dependencyStream = new MemoryStream(metadata.Nuspec, writable: false);
        var dependencyGroups = new NuspecReader(dependencyStream).GetDependencyGroups().ToArray();
        var packageMetadata = document.Root?.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "metadata")
            ?? throw new PackageStorageCorruptionException(
                $"Stored package metadata for '{metadata.Id} {metadata.NormalizedVersion}' has no metadata element.");
        return new TestPackage
        {
            Identity = new PackageIdentity(
                metadata.Id,
                NuGetVersion.Parse(metadata.OriginalVersion)),
            ContentPath = Path.GetFullPath(contentPath),
            StoredContentLength = metadata.ContentLength,
            ExpectedContentSha256 = metadata.Sha256,
            NuspecContent = metadata.Nuspec,
            NormalizedVersion = metadata.NormalizedVersion,
            Description = metadata.Description,
            Summary = Value(packageMetadata, "summary"),
            Title = Value(packageMetadata, "title"),
            Authors = metadata.Authors,
            Tags = metadata.Tags,
            ProjectUrl = OptionalUri(packageMetadata, "projectUrl"),
            Readme = Value(packageMetadata, "readme"),
            Icon = Value(packageMetadata, "icon"),
            LicenseExpression = LicenseValue(packageMetadata, "expression"),
            LicenseFile = LicenseValue(packageMetadata, "file"),
            LicenseUrl = OptionalUri(packageMetadata, "licenseUrl"),
            PackageTypes = packageMetadata.Descendants()
                .Where(element => element.Name.LocalName == "packageType")
                .Select(element => new PackageTypeMetadata(
                    element.Attribute("name")?.Value ?? string.Empty,
                    element.Attribute("version")?.Value ?? string.Empty))
                .Where(packageType => !string.IsNullOrWhiteSpace(packageType.Name))
                .ToArray(),
            Repository = ParseRepository(packageMetadata),
            PackageHash = metadata.PackageHash,
            RepositoryMetadata = metadata.RepositoryMetadata ??
                new PackageRepositoryMetadata(
                    SplitOwners(metadata.Authors),
                    Downloads: 0,
                    Verified: false,
                    Deprecation: null),
            DependencyGroups = dependencyGroups,
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

        if (ExpectedContentSha256 is not null)
        {
            using var verificationStream = OpenFile(ContentPath!);
            var actualHash = SHA256.HashData(verificationStream);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, ExpectedContentSha256))
            {
                throw new PackageStorageCorruptionException(
                    $"Package storage is corrupt for '{Identity.Id} {NormalizedVersion}': " +
                    "blob SHA-256 does not match durable metadata.");
            }
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
        stream.Position = 0;
        var packageHash = Convert.ToBase64String(SHA512.HashData(stream));
        stream.Position = 0;
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
            Summary = Value(metadata, "summary"),
            Title = Value(metadata, "title"),
            Authors = Value(metadata, "authors"),
            Tags = Value(metadata, "tags"),
            ProjectUrl = OptionalUri(metadata, "projectUrl"),
            Readme = Value(metadata, "readme"),
            Icon = Value(metadata, "icon"),
            LicenseExpression = LicenseValue(metadata, "expression"),
            LicenseFile = LicenseValue(metadata, "file"),
            LicenseUrl = OptionalUri(metadata, "licenseUrl"),
            PackageTypes = metadata.Descendants()
                .Where(element => element.Name.LocalName == "packageType")
                .Select(element => new PackageTypeMetadata(
                    element.Attribute("name")?.Value ?? string.Empty,
                    element.Attribute("version")?.Value ?? string.Empty))
                .Where(packageType => !string.IsNullOrWhiteSpace(packageType.Name))
                .ToArray(),
            Repository = ParseRepository(metadata),
            PackageHash = packageHash,
            RepositoryMetadata = new PackageRepositoryMetadata(
                SplitOwners(Value(metadata, "authors")),
                Downloads: 0,
                Verified: false,
                Deprecation: null),
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

    private static Uri? OptionalUri(XElement metadata, string name)
    {
        var value = Value(metadata, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string LicenseValue(XElement metadata, string type)
    {
        var license = metadata.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "license" &&
            string.Equals(element.Attribute("type")?.Value, type, StringComparison.OrdinalIgnoreCase));
        return license?.Value ?? string.Empty;
    }

    private static RepositoryMetadata? ParseRepository(XElement metadata)
    {
        var repository = metadata.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "repository");
        return repository is null
            ? null
            : new RepositoryMetadata(
                repository.Attribute("type")?.Value ?? string.Empty,
                repository.Attribute("url")?.Value ?? string.Empty,
                repository.Attribute("commit")?.Value ?? string.Empty,
                repository.Attribute("branch")?.Value ?? string.Empty);
    }

    private static IReadOnlyList<string> SplitOwners(string authors) =>
        authors.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed record PackageTypeMetadata(string Name, string Version);

public sealed record RepositoryMetadata(string Type, string Url, string Commit, string Branch);

public sealed record PackageRepositoryMetadata(
    IReadOnlyList<string> Owners,
    long Downloads,
    bool Verified,
    PackageDeprecation? Deprecation);

public sealed record PackageDeprecation(
    IReadOnlyList<string> Reasons,
    string Message,
    AlternatePackage? AlternatePackage);

public sealed record AlternatePackage(string Id, string Range);

public sealed class InvalidPackageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class PackageLimitExceededException(string message) : Exception(message);

public sealed class DuplicatePackageException(string id, string version)
    : Exception($"Package '{id} {version}' already exists.");
