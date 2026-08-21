using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Kernel.Owners.PackageMetadata;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Kernel.Owners.Registration;

internal sealed class RegistrationDocumentBuilder(IVulnerabilityReadCapability vulnerabilities)
{
    public RegistrationPageDocument CreatePage(
        IReadOnlyList<CapabilityPackageMetadata> packages)
    {
        var first = packages[0];
        var last = packages[^1];
        var normalizedId = first.Id.ToLowerInvariant();
        return new RegistrationPageDocument(
            RouteReference.Endpoint(
                "registration.page",
                RouteParameterValue.PackageId("id", normalizedId),
                RouteParameterValue.PackageVersion("lower", first.NormalizedVersion),
                RouteParameterValue.PackageVersion("upper", last.NormalizedVersion)),
            CreateIndexReference(normalizedId),
            packages.Count,
            first.NormalizedVersion,
            last.NormalizedVersion,
            [.. packages.Select(CreateLeaf)]);
    }

    public RegistrationLeafDocument CreateLeaf(CapabilityPackageMetadata package)
    {
        var id = package.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        var advisories = vulnerabilities.Active.Find(package.Id, package.Version);
        return new RegistrationLeafDocument(
            CreateLeafReference(id, version),
            CreateIndexReference(id),
            RouteReference.Endpoint(
                "flatcontainer.content",
                RouteParameterValue.PackageId("id", id),
                RouteParameterValue.PackageVersion("version", version),
                RouteParameterValue.Text("fileName", $"{id}.{version}.nupkg")),
            new PackageIdentity(package.Id, version),
            package.Authors,
            [.. package.RepositoryMetadata.Owners],
            package.RepositoryMetadata.Downloads,
            package.Description,
            package.Summary,
            string.IsNullOrEmpty(package.Title) ? package.Id : package.Title,
            [.. package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            package.ProjectUrl?.OriginalString,
            package.Readme,
            package.Icon,
            package.LicenseExpression,
            package.LicenseFile,
            package.LicenseUrl?.OriginalString,
            PackageMetadataDocumentBuilder.CreatePackageTypes(package),
            package.Repository is null
                ? null
                : new PackageRepositoryDocument(
                    package.Repository.Type,
                    package.Repository.Url,
                    package.Repository.Commit,
                    package.Repository.Branch),
            package.IsListed,
            package.Published,
            [
                .. package.DependencyGroups.Select(group => new PackageDependencyGroupDocument(
                    group.TargetFramework.GetShortFolderName(),
                    [
                        .. group.Packages.Select(dependency => new PackageDependencyDocument(
                            dependency.Id,
                            dependency.VersionRange.ToNormalizedString()))
                    ]))
            ],
            package.RepositoryMetadata.Deprecation is { } deprecation
                ? new PackageDeprecationDocument(
                    [.. deprecation.Reasons],
                    deprecation.Message,
                    deprecation.AlternatePackage is { } alternate
                        ? new PackageAlternateDocument(alternate.Id, alternate.Range)
                        : null)
                : null,
            [
                .. advisories.Select(advisory => new VulnerabilityAdvisoryDocument(
                    advisory.Url.AbsoluteUri,
                    advisory.Severity.ToString()))
            ]);
    }

    public static RouteReference CreateIndexReference(string id) =>
        RouteReference.Endpoint(
            "registration.index",
            RouteParameterValue.PackageId("id", id));

    private static RouteReference CreateLeafReference(string id, string version) =>
        RouteReference.Endpoint(
            "registration.leaf",
            RouteParameterValue.PackageId("id", id),
            RouteParameterValue.PackageVersion("version", version));
}
