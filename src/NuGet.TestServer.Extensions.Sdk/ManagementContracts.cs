using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Sdk;

internal sealed record PushPackageRequest(StreamHandle Content);

internal sealed record PushPackageResponse(
    PackageIdentity Package,
    PublicationOutcome Outcome);

internal sealed record PackagePublicationDocument(
    PackageIdentity Package,
    PublicationOutcome Outcome,
    string Message);

internal enum PublicationOutcome
{
    Published = 0,
    Quarantined = 1,
    Rejected = 2,
    Duplicate = 3,
    Conflict = 4,
    Unauthorized = 5,
    QuotaExceeded = 6
}

internal sealed record PushSymbolsRequest(StreamHandle Content);

internal sealed record PushSymbolsResponse(PackageIdentity Package);

internal sealed record ListPackagesRequest(
    string? PackageId,
    int Skip,
    int Take);

internal sealed record ListPackagesResponse(ImmutableArray<PackageSummaryDocument> Packages);

internal sealed record PackageSummaryDocument(
    PackageIdentity Package,
    bool Listed,
    DateTimeOffset Published);

internal sealed record UnlistPackageRequest(PackageIdentity Package);

internal sealed record UnlistPackageResponse(PackageIdentity Package);

internal sealed record RelistPackageRequest(PackageIdentity Package);

internal sealed record RelistPackageResponse(PackageIdentity Package);

internal sealed record DeletePackageRequest(
    PackageIdentity Package,
    string Reason);

internal sealed record DeletePackageResponse(PackageIdentity Package);

internal enum PackageMutationOutcome
{
    Succeeded,
    NotFound,
    Forbidden
}

internal sealed record PackageMutationDocument(
    PackageMutationOutcome Outcome,
    string? Detail = null);

internal sealed record ModeratePackageRequest(
    PackageIdentity Package,
    ModerationAction Action,
    string Actor,
    string Reason);

internal sealed record ModeratePackageResponse(
    PackageIdentity Package,
    ModerationAction Action);

internal enum ModerationAction
{
    Approve,
    Reject,
    Quarantine,
    Delete
}

internal sealed record GetModerationAuditRequest(
    long? AfterSequence,
    int Take);

internal sealed record GetModerationAuditResponse(
    ImmutableArray<ModerationAuditDocument> Events);

internal sealed record ModerationAuditDocument(
    long Sequence,
    PackageIdentity Package,
    ModerationAction Action,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAt);

internal sealed record GetPackageValidationsRequest(PackageIdentity Package);

internal sealed record GetPackageValidationsResponse(
    ImmutableArray<PackageValidationDocument> Validations);

internal sealed record PackageValidationDocument(
    string Validator,
    string Outcome,
    string? Detail);
