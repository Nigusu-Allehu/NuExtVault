using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

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
