using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Registration;

internal sealed class RegistrationDocumentBuilder(
    IRegistrationVulnerabilityReadCapability vulnerabilities,
    IDocumentContributionSource contributions)
{
    public async ValueTask<RegistrationPageDocument> CreatePageAsync(
        IReadOnlyList<RegistrationPackageMetadata> packages,
        CancellationToken cancellationToken)
    {
        var first = packages[0];
        var last = packages[^1];
        var normalizedId = first.Package.Id.ToLowerInvariant();
        var leaves = new List<RegistrationLeafDocument>(packages.Count);
        foreach (var package in packages)
        {
            leaves.Add(await CreateLeafAsync(package, cancellationToken));
        }

        return new RegistrationPageDocument(
            RouteReference.Endpoint(
                "registration.page",
                RouteParameterValue.PackageId("id", normalizedId),
                RouteParameterValue.PackageVersion("lower", first.Package.Version),
                RouteParameterValue.PackageVersion("upper", last.Package.Version)),
            CreateIndexReference(normalizedId),
            packages.Count,
            first.Package.Version,
            last.Package.Version,
            [.. leaves]);
    }

    public async ValueTask<RegistrationLeafDocument> CreateLeafAsync(
        RegistrationPackageMetadata package,
        CancellationToken cancellationToken)
    {
        var id = package.Package.Id.ToLowerInvariant();
        var version = package.Package.Version;
        var advisories = await vulnerabilities.FindAsync(package.Package, cancellationToken);
        var extensionValues =
            ImmutableSortedDictionary.CreateBuilder<string, RegistrationLeafExtensionDocument>(
                StringComparer.Ordinal);
        foreach (var contributor in contributions.Get<
                     RegistrationLeafContributionContext,
                     RegistrationLeafExtensionDocument>(
                     RegistrationContributionPoints.Leaf,
                     RegistrationContributionPoints.LeafContractV1))
        {
            extensionValues.Add(
                contributor.Namespace,
                await contributor.Contributor.ContributeAsync(
                    new RegistrationLeafContributionContext(package.Package),
                    cancellationToken));
        }

        return new RegistrationLeafDocument(
            CreateLeafReference(id, version),
            CreateIndexReference(id),
            RouteReference.Endpoint(
                "flatcontainer.content",
                RouteParameterValue.PackageId("id", id),
                RouteParameterValue.PackageVersion("version", version),
                RouteParameterValue.Text("fileName", $"{id}.{version}.nupkg")),
            package.Package,
            package.Authors,
            package.Owners,
            package.Downloads,
            package.Description,
            package.Summary,
            package.Title,
            package.Tags,
            package.ProjectUrl,
            package.Readme,
            package.Icon,
            package.LicenseExpression,
            package.LicenseFile,
            package.LicenseUrl,
            package.PackageTypes,
            package.Repository,
            package.Listed,
            package.Published,
            package.DependencyGroups,
            package.Deprecation,
            advisories,
            extensionValues.ToImmutable());
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
