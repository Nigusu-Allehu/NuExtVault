using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Search;

internal static class SearchDocumentBuilder
{
    public static SearchResultDocument CreateResult(IndexedPackageSearchItem item)
    {
        var package = item.Package;
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
            package.Owners,
            [.. package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            package.ProjectUrl,
            item.Versions.Sum(candidate => candidate.Downloads),
            package.Verified,
            package.PackageTypes,
            [
                .. item.Versions.Select(candidate => new SearchVersionDocument(
                    candidate.NormalizedVersion,
                    candidate.Downloads,
                    CreateRegistrationLeafReference(id, candidate.NormalizedVersion)))
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
