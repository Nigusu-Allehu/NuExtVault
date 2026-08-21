using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Kernel.Owners.Search;

internal interface ISearchPackageQuery
{
    ValueTask<CapabilityPackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        string? packageType,
        CancellationToken token);
}

internal sealed class SearchPackageQuery(IPackageReadCapability packages) : ISearchPackageQuery
{
    public ValueTask<CapabilityPackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        string? packageType,
        CancellationToken token) =>
        packages.SearchAsync(
            query,
            includePrerelease,
            skip,
            take,
            packageType,
            token);
}
