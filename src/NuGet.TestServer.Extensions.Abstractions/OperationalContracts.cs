using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

internal sealed record GetControlStateRequest;

internal sealed record GetControlStateResponse(
    int PackageCount,
    int FaultCount,
    int FaultCapacity,
    int RequestCount,
    int RequestCapacity,
    long EvictedRequestCount);

internal sealed record ResetControlStateRequest;

internal sealed record ResetControlStateResponse;

internal sealed record GetControlPackagesRequest;

internal sealed record GetControlPackagesResponse(
    ImmutableArray<PackageSummaryDocument> Packages);

internal sealed record AddControlPackageRequest(StreamHandle Content);

internal sealed record AddControlPackageResponse(PackageSummaryDocument Package);

internal sealed record DeleteControlPackageRequest(PackageIdentity Package);

internal sealed record DeleteControlPackageResponse(PackageIdentity Package);

internal sealed record RelistControlPackageRequest(PackageIdentity Package);

internal sealed record RelistControlPackageResponse(PackageIdentity Package);

internal sealed record UnlistControlPackageRequest(PackageIdentity Package);

internal sealed record UnlistControlPackageResponse(PackageIdentity Package);

internal sealed record UpdatePackageMetadataRequest(
    PackageIdentity Package,
    PackageRepositoryMetadataDocument Metadata);

internal sealed record UpdatePackageMetadataResponse(PackageIdentity Package);

internal sealed record PackageRepositoryMetadataDocument(
    ImmutableArray<string> Owners,
    long Downloads,
    bool Verified,
    PackageDeprecationDocument? Deprecation);

internal sealed record PackageDeprecationDocument(
    ImmutableArray<string> Reasons,
    string? Message,
    PackageAlternateDocument? AlternatePackage);

internal sealed record PackageAlternateDocument(string Id, string Range);

internal sealed record GetRequestsRequest;

internal sealed record GetRequestsResponse(ImmutableArray<RequestRecordDocument> Requests);

internal sealed record RequestRecordDocument(
    long Sequence,
    DateTimeOffset OccurredAt,
    string Method,
    string Route,
    int StatusCode,
    long ElapsedMilliseconds,
    string? FaultRuleId,
    string? Identity);

internal sealed record ClearRequestsRequest;

internal sealed record ClearRequestsResponse;

internal sealed record GetFaultsRequest;

internal sealed record GetFaultsResponse(ImmutableArray<FaultRuleDocument> Faults);

internal sealed record AddFaultRequest(FaultRuleDocument Fault);

internal sealed record AddFaultResponse(FaultRuleDocument Fault);

internal sealed record ClearFaultsRequest;

internal sealed record ClearFaultsResponse;

internal sealed record FaultRuleDocument(
    string Id,
    string Method,
    string RoutePattern,
    int StatusCode,
    long DelayMilliseconds,
    int? RemainingMatches);

internal sealed record GetLivenessRequest;

internal sealed record GetLivenessResponse(string Status, string Mode);

internal sealed record GetReadinessRequest;

internal sealed record GetReadinessResponse(
    string Status,
    string? Dependency,
    bool Ready);

internal sealed record GetStorageHealthRequest;

internal sealed record GetStorageHealthResponse(
    string Status,
    ImmutableArray<StorageHealthItemDocument> Items);

internal sealed record StorageHealthItemDocument(
    string Name,
    string Status,
    string? Detail);

internal sealed record GetDiagnosticsRequest;

internal sealed record GetDiagnosticsResponse(
    long RequestCount,
    long FailedRequestCount,
    long PublishedPackageCount,
    long StorageFailureCount);

internal sealed record CreateBackupRequest(
    StreamHandle Destination,
    string RequestedBy);

internal sealed record CreateBackupResponse(BackupManifestDocument Manifest);

internal sealed record RestoreBackupRequest(
    StreamHandle Source,
    string RequestedBy);

internal sealed record RestoreBackupResponse(BackupManifestDocument Manifest);

internal sealed record BackupManifestDocument(
    int Version,
    DateTimeOffset CreatedAt,
    ImmutableArray<BackupEntryDocument> Entries);

internal sealed record BackupEntryDocument(
    string LogicalName,
    long Length,
    string Sha256);
