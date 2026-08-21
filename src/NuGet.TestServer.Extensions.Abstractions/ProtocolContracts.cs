using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

internal sealed record GetServiceIndexRequest;

internal sealed record GetServiceIndexResponse(
    string Version,
    ImmutableArray<ServiceResourceDescriptor> Resources);

internal sealed record ServiceResourceDescriptor(
    RouteReference Route,
    string ResourceType,
    string? Comment);

internal sealed record ServiceResourceContribution(
    string ResourceType,
    string Version,
    OperationId OperationId,
    string RouteName,
    ServiceResourceVisibility Visibility,
    ServiceResourceAccess RequiredAccess,
    ImmutableArray<string> ProducesUrlsFor,
    ImmutableArray<string> RequiresResourceTypes,
    string? Comment,
    int Order,
    ServiceResourceReadiness Readiness)
{
    public string AdvertisedType => $"{ResourceType}/{Version}";
}

internal enum ServiceResourceVisibility
{
    Advertised,
    Hidden
}

internal enum ServiceResourceAccess
{
    Read,
    Write,
    PackagePublish
}

internal enum ServiceResourceReadiness
{
    NotReady,
    Ready
}

internal sealed record GetPackageVersionsRequest(string PackageId);

internal sealed record GetPackageVersionsResponse(ImmutableArray<string> Versions);

internal sealed record GetPackageRequest(PackageIdentity Package);

internal sealed record GetPackageResponse(ContentDescriptor Package);

internal sealed record GetNuspecRequest(PackageIdentity Package);

internal sealed record GetNuspecResponse(ContentDescriptor Nuspec);

internal sealed record GetPackageHashRequest(PackageIdentity Package);

internal sealed record GetPackageHashResponse(string Sha512);

internal sealed record GetSymbolRequest(PackageIdentity Package);

internal sealed record GetSymbolResponse(ContentDescriptor Symbols);

internal sealed record GetRegistrationIndexRequest(string PackageId);

internal sealed record GetRegistrationIndexResponse(
    RouteReference Id,
    int Count,
    ImmutableArray<RegistrationPageDocument> Items);

internal sealed record RegistrationPageDocument(
    RouteReference Id,
    RouteReference Parent,
    int Count,
    string Lower,
    string Upper,
    ImmutableArray<RegistrationLeafDocument> Items);

internal sealed record GetRegistrationPageRequest(
    string PackageId,
    string Lower,
    string Upper);

internal sealed record GetRegistrationPageResponse(RegistrationPageDocument Page);

internal sealed record GetRegistrationLeafRequest(PackageIdentity Package);

internal sealed record GetRegistrationLeafResponse(RegistrationLeafDocument Leaf);

internal sealed record RegistrationLeafDocument(
    RouteReference Id,
    RouteReference Registration,
    RouteReference PackageContent,
    PackageIdentity Package,
    string Authors,
    ImmutableArray<string> Owners,
    long Downloads,
    string Description,
    string? Summary,
    string? Title,
    ImmutableArray<string> Tags,
    string? ProjectUrl,
    string? Readme,
    string? Icon,
    string? LicenseExpression,
    string? LicenseFile,
    string? LicenseUrl,
    ImmutableArray<PackageTypeDocument> PackageTypes,
    PackageRepositoryDocument? Repository,
    bool Listed,
    DateTimeOffset Published,
    ImmutableArray<PackageDependencyGroupDocument> DependencyGroups,
    PackageDeprecationDocument? Deprecation,
    ImmutableArray<VulnerabilityAdvisoryDocument> Vulnerabilities);

internal sealed record PackageTypeDocument(string Name, string Version);

internal sealed record PackageDependencyGroupDocument(
    string TargetFramework,
    ImmutableArray<PackageDependencyDocument> Dependencies);

internal sealed record PackageDependencyDocument(string Id, string Range);

internal sealed record PackageRepositoryDocument(
    string? Type,
    string? Url,
    string? Commit,
    string? Branch);

internal sealed record VulnerabilityAdvisoryDocument(string AdvisoryUrl, string Severity);

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

internal sealed record GetVulnerabilityIndexRequest;

internal sealed record GetVulnerabilityIndexResponse(
    string SnapshotId,
    DateTimeOffset UpdatedAt,
    ImmutableArray<VulnerabilityPageDescriptor> Pages);

internal sealed record VulnerabilityPageDescriptor(
    string Name,
    RouteReference Route,
    string Sha256,
    DateTimeOffset UpdatedAt,
    string? Comment);

internal sealed record GetVulnerabilityPageRequest(
    string SnapshotId,
    string PageName);

internal sealed record GetVulnerabilityPageResponse(ContentDescriptor Page);
