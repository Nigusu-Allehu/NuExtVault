using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Sdk;

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
