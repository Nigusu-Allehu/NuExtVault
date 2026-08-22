using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NuGet.TestServer.Extensions.Sdk;

namespace NuTest.PackageStaging;

/// <summary>The lifecycle of one staging group.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StagingGroupStatus>))]
public enum StagingGroupStatus
{
    Active,
    Expired
}

/// <summary>The lifecycle of one staged package inside a group.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StagedPackageStatus>))]
public enum StagedPackageStatus
{
    Staged,
    Promoted,
    Rejected,
    Expired
}

/// <summary>The outcome contract every staging operation reports.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StagingOutcome>))]
public enum StagingOutcome
{
    Succeeded,
    GroupNotFound,
    GroupInactive,
    GroupExpired,
    PackageNotFound,
    AlreadyResolved,
    QuotaExceeded,
    ContentTooLarge,
    InvalidContent,
    IdentityMismatch,
    Conflict,
    DuplicatePackage,
    RejectedByPolicy,
    Quarantined,
    Unauthorized,
    Canceled,
    Failed
}

/// <summary>One staged package record inside a group.</summary>
public sealed record StagedPackageRecord(
    string PackageId,
    string Version,
    string ContentHandleId,
    string ContentSha256,
    long ContentLength,
    string? SymbolHandleId,
    StagedPackageStatus Status,
    DateTimeOffset StagedAt,
    DateTimeOffset? ResolvedAt,
    string? UploadIdempotencyKey,
    string? SymbolUploadIdempotencyKey,
    string? SymbolContentSha256,
    string? PromotionIdempotencyKey,
    string? Detail);

/// <summary>The authoritative staging-group document held in kernel extension state.</summary>
public sealed record StagingGroupState(
    string GroupId,
    StagingGroupStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaximumPackages,
    ImmutableArray<StagedPackageRecord> Packages);

/// <summary>A staging group projected for a caller, with its concurrency token.</summary>
public sealed record StagingGroupView(
    string GroupId,
    StagingGroupStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaximumPackages,
    long ConcurrencyToken,
    ImmutableArray<StagedPackageRecord> Packages);

public sealed record CreateGroupRequest(string GroupId, int? MaximumPackages, int? TtlMinutes);

public sealed record CreateGroupResponse(
    StagingOutcome Outcome,
    string GroupId,
    StagingGroupStatus Status,
    DateTimeOffset ExpiresAt,
    string? Detail);

public sealed record ListGroupsRequest(int Take);

public sealed record ListGroupsResponse(
    StagingOutcome Outcome,
    ImmutableArray<StagingGroupView> Groups);

public sealed record GetGroupRequest(string GroupId);

public sealed record GetGroupResponse(
    StagingOutcome Outcome,
    StagingGroupView? Group,
    string? Detail);

public sealed record UploadPackageRequest(
    string GroupId,
    string? IdempotencyKey,
    StreamHandle Content);

public sealed record UploadPackageResponse(
    StagingOutcome Outcome,
    string? PackageId,
    string? Version,
    string? ContentSha256,
    long ContentLength,
    string? Detail);

public sealed record UploadSymbolRequest(
    string GroupId,
    string PackageId,
    string Version,
    string? IdempotencyKey,
    StreamHandle Content);

public sealed record UploadSymbolResponse(
    StagingOutcome Outcome,
    string? PackageId,
    string? Version,
    string? ContentSha256,
    string? Detail);

public sealed record InspectRequest(string GroupId, string PackageId, string Version);

public sealed record InspectResponse(
    StagingOutcome Outcome,
    StagedPackageRecord? Package,
    string? Detail);

public sealed record PromoteRequest(
    string GroupId,
    string PackageId,
    string Version,
    string? IdempotencyKey);

public sealed record PromoteResponse(
    StagingOutcome Outcome,
    string? PackageId,
    string? Version,
    bool Replayed,
    string? Detail);

public sealed record RejectRequest(string GroupId, string PackageId, string Version, string Reason);

public sealed record RejectResponse(
    StagingOutcome Outcome,
    StagedPackageStatus? Status,
    string? Detail);

public sealed record ExpireRequest(string GroupId);

public sealed record ExpireResponse(
    StagingOutcome Outcome,
    int ExpiredPackages,
    string? Detail);
