using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

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
