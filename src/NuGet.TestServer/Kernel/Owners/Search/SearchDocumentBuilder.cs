using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Kernel.Owners.PackageMetadata;

namespace NuGet.TestServer.Kernel.Owners.Search;

internal static class SearchDocumentBuilder
{
    public static SearchResultDocument CreateResult(
        CapabilityPackageMetadata package,
        IReadOnlyList<CapabilityPackageMetadata> versions)
    {
        var id = package.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        return new SearchResultDocument(
            CreateRegistrationLeafReference(id, version),
            CreateRegistrationIndexReference(id),
            new PackageIdentity(package.Id, version),
            package.Description,
            string.IsNullOrEmpty(package.Summary) ? package.Description : package.Summary,
            string.IsNullOrEmpty(package.Title) ? package.Id : package.Title,
            [package.Authors],
            [.. package.RepositoryMetadata.Owners],
            [.. package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            package.ProjectUrl?.OriginalString,
            versions.Sum(item => item.RepositoryMetadata.Downloads),
            package.RepositoryMetadata.Verified,
            PackageMetadataDocumentBuilder.CreatePackageTypes(package),
            [
                .. versions.Select(item => new SearchVersionDocument(
                    item.NormalizedVersion,
                    item.RepositoryMetadata.Downloads,
                    CreateRegistrationLeafReference(id, item.NormalizedVersion)))
            ]);
    }

    private static RouteReference CreateRegistrationIndexReference(string id) =>
        RouteReference.Endpoint(
            "registration.index",
            RouteParameterValue.PackageId("id", id));

    private static RouteReference CreateRegistrationLeafReference(string id, string version) =>
        RouteReference.Endpoint(
            "registration.leaf",
            RouteParameterValue.PackageId("id", id),
            RouteParameterValue.PackageVersion("version", version));
}
