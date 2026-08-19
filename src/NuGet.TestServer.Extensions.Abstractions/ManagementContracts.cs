using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

internal sealed record PushPackageRequest(
    StreamHandle Content,
    string Actor,
    string Source,
    bool IsAdministrator);

internal sealed record PushPackageResponse(
    PackageIdentity Package,
    PublicationOutcome Outcome);

internal enum PublicationOutcome
{
    Published,
    Duplicate,
    Quarantined,
    Rejected,
    Unauthorized,
    QuotaExceeded,
    Conflict
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

internal sealed record UnlistPackageRequest(PackageIdentity Package, string Actor);

internal sealed record UnlistPackageResponse(PackageIdentity Package);

internal sealed record RelistPackageRequest(PackageIdentity Package, string Actor);

internal sealed record RelistPackageResponse(PackageIdentity Package);

internal sealed record DeletePackageRequest(
    PackageIdentity Package,
    string Actor,
    string Reason);

internal sealed record DeletePackageResponse(PackageIdentity Package);

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
