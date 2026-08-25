using System.Collections.Immutable;
using System.Text.Json;

namespace NuExtVault.Extensions.Sdk;

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
    ImmutableArray<VulnerabilityAdvisoryDocument> Vulnerabilities,
    ImmutableSortedDictionary<string, RegistrationLeafExtensionDocument> Extensions);

internal static class RegistrationContributionPoints
{
    public const string Leaf = "NuGet.Registration.BuildLeaf";
    public const string LeafContractV1 = "registration-leaf-v1";
}

internal sealed record RegistrationLeafContributionContext(PackageIdentity Package);

internal sealed record RegistrationLeafExtensionDocument(JsonElement Value);

internal sealed record RegistrationPackageMetadata(
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
    PackageDeprecationDocument? Deprecation);

internal interface IRegistrationMetadataReadCapability
{
    ValueTask<ImmutableArray<RegistrationPackageMetadata>> FindByIdAsync(
        string packageId,
        CancellationToken cancellationToken);

    ValueTask<RegistrationPackageMetadata?> FindLeafAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken);
}

internal interface IRegistrationVulnerabilityReadCapability
{
    ValueTask<ImmutableArray<VulnerabilityAdvisoryDocument>> FindAsync(
        PackageIdentity package,
        CancellationToken cancellationToken);
}

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
