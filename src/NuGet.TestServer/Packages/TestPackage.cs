using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed record TestPackage
{
    public required PackageIdentity Identity { get; init; }
    public required byte[] Content { get; init; }
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
                Authors = Value(metadata, "authors"),
                Tags = Value(metadata, "tags"),
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
}

public sealed class InvalidPackageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class DuplicatePackageException(string id, string version)
    : Exception($"Package '{id} {version}' already exists.");
