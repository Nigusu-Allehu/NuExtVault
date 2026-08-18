using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed record TestPackage
{
    private static readonly IReadOnlyList<PackageTypeMetadata> DefaultPackageTypes =
        [new("Dependency", string.Empty)];

    public required PackageIdentity Identity { get; init; }
    public required byte[] Content { get; init; }
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

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var nuspecEntry = archive.Entries.SingleOrDefault(entry =>
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The package does not contain a nuspec.");
            using var nuspecStream = nuspecEntry.Open();
            using var nuspecBuffer = new MemoryStream();
            nuspecStream.CopyTo(nuspecBuffer);
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
                Content = content.ToArray(),
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
                PackageHash = Convert.ToBase64String(SHA512.HashData(content)),
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
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidPackageException("The content is not a valid NuGet package.", exception);
        }
    }

    public static string NormalizeVersion(NuGetVersion version)
    {
        return version.ToNormalizedString().Split('+', 2)[0].ToLowerInvariant();
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

public sealed class DuplicatePackageException(string id, string version)
    : Exception($"Package '{id} {version}' already exists.");
