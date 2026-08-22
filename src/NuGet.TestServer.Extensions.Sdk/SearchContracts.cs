using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Sdk;

internal sealed record SearchRequest(
    string Query,
    int Skip,
    int Take,
    bool IncludePrerelease,
    string? PackageType);

internal sealed record SearchResponse(
    long TotalHits,
    ImmutableArray<SearchResultDocument> Data);

internal sealed record SearchResultDocument(
    RouteReference Id,
    RouteReference Registration,
    PackageIdentity Package,
    string Description,
    string? Summary,
    string? Title,
    ImmutableArray<string> Authors,
    ImmutableArray<string> Owners,
    ImmutableArray<string> Tags,
    string? ProjectUrl,
    long TotalDownloads,
    bool Verified,
    ImmutableArray<PackageTypeDocument> PackageTypes,
    ImmutableArray<SearchVersionDocument> Versions);

internal sealed record SearchVersionDocument(
    string Version,
    long Downloads,
    RouteReference Id);

internal sealed record IndexedPackageSearchRequest(
    string Query,
    bool IncludePrerelease,
    int Skip,
    int Take,
    string? PackageType);

internal sealed record IndexedPackageSearchPage(
    int TotalHits,
    ImmutableArray<IndexedPackageSearchItem> Items);

internal sealed record IndexedPackageSearchItem(
    IndexedPackageMetadata Package,
    ImmutableArray<IndexedPackageMetadata> Versions);

internal sealed record IndexedPackageMetadata(
    string Id,
    string NormalizedVersion,
    string Description,
    string Summary,
    string Title,
    string Authors,
    string Tags,
    string? ProjectUrl,
    ImmutableArray<string> Owners,
    long Downloads,
    bool Verified,
    ImmutableArray<PackageTypeDocument> PackageTypes);

internal interface ISearchIndexQueryCapability
{
    ValueTask<IndexedPackageSearchPage> QueryAsync(
        IndexedPackageSearchRequest request,
        CancellationToken token);
}
