using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Registration and search owners. Documents are produced as typed contracts first
/// and then rendered into the current protocol shape.
/// </summary>
internal sealed class RegistrationSearchOperations(
    IPackageReadCapability packages,
    IVulnerabilityReadCapability vulnerabilities)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetRegistrationIndexRequest, GetRegistrationIndexResponse>(
                OperationIds.RegistrationGetIndex,
                GetIndexAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetRegistrationPageRequest, GetRegistrationPageResponse>(
                OperationIds.RegistrationGetPage,
                GetPageAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
                OperationIds.RegistrationGetLeaf,
                GetLeafAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<SearchRequest, SearchResponse>(
                OperationIds.SearchQuery,
                SearchAsync));
    }

    private async ValueTask<OperationResponse<GetRegistrationIndexResponse>> GetIndexAsync(
        GetRegistrationIndexRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var packages = await FindRegistrationCandidatesAsync(request.PackageId, token);
        if (packages.Length == 0)
        {
            return OperationResponse<GetRegistrationIndexResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.PackageId}' has no registration."));
        }

        var normalizedId = packages[0].Id.ToLowerInvariant();
        var response = new GetRegistrationIndexResponse(
            RegistrationIndex(normalizedId),
            1,
            [CreatePage(packages)]);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(new Dictionary<string, object?>
            {
                ["@id"] = response.Id,
                ["count"] = response.Count,
                ["items"] = response.Items.Select(RenderPage).ToArray()
            })));
        return OperationResponse<GetRegistrationIndexResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetRegistrationPageResponse>> GetPageAsync(
        GetRegistrationPageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var packages = await FindRegistrationCandidatesAsync(request.PackageId, token);
        if (!MatchesBounds(packages, request.Lower, request.Upper))
        {
            return OperationResponse<GetRegistrationPageResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Registration page '{request.Lower}'-'{request.Upper}' does not exist."));
        }

        var response = new GetRegistrationPageResponse(CreatePage(packages));
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(RenderPage(response.Page))));
        return OperationResponse<GetRegistrationPageResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetRegistrationLeafResponse>> GetLeafAsync(
        GetRegistrationLeafRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var package = await packages.FindReadableAsync(
            request.Package.Id,
            request.Package.Version,
            PackageResourceClass.Registration,
            token);
        if (package is null)
        {
            return OperationResponse<GetRegistrationLeafResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.Package.Id}' version " +
                    $"'{request.Package.Version}' has no registration."));
        }

        var response = new GetRegistrationLeafResponse(
            CreateLeaf(package));
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(RenderLeaf(response.Leaf))));
        return OperationResponse<GetRegistrationLeafResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<SearchResponse>> SearchAsync(
        SearchRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var page = await packages.SearchAsync(
            request.Query,
            request.IncludePrerelease,
            request.Skip,
            request.Take,
            request.PackageType,
            token);
        var response = new SearchResponse(
            page.TotalHits,
            [
                .. page.Items.Select(item => CreateSearchResult(item.Package, item.Versions))
            ]);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(new
            {
                totalHits = response.TotalHits,
                data = response.Data.Select(RenderSearchResult).ToArray()
            })));
        return OperationResponse<SearchResponse>.Success(response);
    }

    private async ValueTask<CapabilityPackageMetadata[]> FindRegistrationCandidatesAsync(
        string packageId,
        CancellationToken token) =>
        [
            .. await packages.FindReadableStoredByIdAsync(
                packageId,
                PackageResourceClass.Registration,
                token)
        ];

    private RegistrationPageDocument CreatePage(
        IReadOnlyList<CapabilityPackageMetadata> packages)
    {
        var first = packages[0];
        var last = packages[^1];
        var normalizedId = first.Id.ToLowerInvariant();
        return new RegistrationPageDocument(
            RouteReference.Endpoint(
                "registration.page",
                RouteParameterValue.PackageId("id", normalizedId),
                RouteParameterValue.PackageVersion("lower", first.NormalizedVersion),
                RouteParameterValue.PackageVersion("upper", last.NormalizedVersion)),
            RegistrationIndex(normalizedId),
            packages.Count,
            first.NormalizedVersion,
            last.NormalizedVersion,
            [.. packages.Select(CreateLeaf)]);
    }

    private RegistrationLeafDocument CreateLeaf(CapabilityPackageMetadata package)
    {
        var id = package.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        var advisories = vulnerabilities.Active.Find(
            package.Id,
            package.Version);
        return new RegistrationLeafDocument(
            RegistrationLeaf(id, version),
            RegistrationIndex(id),
            RouteReference.Endpoint(
                "flatcontainer.content",
                RouteParameterValue.PackageId("id", id),
                RouteParameterValue.PackageVersion("version", version),
                RouteParameterValue.Text("fileName", $"{id}.{version}.nupkg")),
            new PackageIdentity(package.Id, version),
            package.Authors,
            [.. package.RepositoryMetadata.Owners],
            package.RepositoryMetadata.Downloads,
            package.Description,
            package.Summary,
            string.IsNullOrEmpty(package.Title) ? package.Id : package.Title,
            [.. package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            package.ProjectUrl?.OriginalString,
            package.Readme,
            package.Icon,
            package.LicenseExpression,
            package.LicenseFile,
            package.LicenseUrl?.OriginalString,
            [
                .. package.EffectivePackageTypes.Select(
                    type => new PackageTypeDocument(type.Name, type.Version))
            ],
            package.Repository is null
                ? null
                : new PackageRepositoryDocument(
                    package.Repository.Type,
                    package.Repository.Url,
                    package.Repository.Commit,
                    package.Repository.Branch),
            package.IsListed,
            package.Published,
            [
                .. package.DependencyGroups.Select(group => new PackageDependencyGroupDocument(
                    group.TargetFramework.GetShortFolderName(),
                    [
                        .. group.Packages.Select(dependency => new PackageDependencyDocument(
                            dependency.Id,
                            dependency.VersionRange.ToNormalizedString()))
                    ]))
            ],
            package.RepositoryMetadata.Deprecation is { } deprecation
                ? new PackageDeprecationDocument(
                    [.. deprecation.Reasons],
                    deprecation.Message,
                    deprecation.AlternatePackage is { } alternate
                        ? new PackageAlternateDocument(alternate.Id, alternate.Range)
                        : null)
                : null,
            [
                .. advisories.Select(advisory => new VulnerabilityAdvisoryDocument(
                    advisory.Url.AbsoluteUri,
                    advisory.Severity.ToString()))
            ]);
    }

    private static SearchResultDocument CreateSearchResult(
        CapabilityPackageMetadata package,
        IReadOnlyList<CapabilityPackageMetadata> versions)
    {
        var id = package.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        return new SearchResultDocument(
            RegistrationLeaf(id, version),
            RegistrationIndex(id),
            new PackageIdentity(package.Id, version),
            package.Description,
            string.IsNullOrEmpty(package.Summary) ? package.Description : package.Summary,
            string.IsNullOrEmpty(package.Title) ? package.Id : package.Title,
            [package.Authors],
            [.. package.RepositoryMetadata.Owners],
            [.. package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            package.ProjectUrl?.OriginalString,
            versions.Sum(item => item.RepositoryMetadata.Downloads),
            package.RepositoryMetadata.Verified,
            [
                .. package.EffectivePackageTypes.Select(
                    type => new PackageTypeDocument(type.Name, type.Version))
            ],
            [
                .. versions.Select(item => new SearchVersionDocument(
                    item.NormalizedVersion,
                    item.RepositoryMetadata.Downloads,
                    RegistrationLeaf(id, item.NormalizedVersion)))
            ]);
    }

    private static Dictionary<string, object?> RenderPage(RegistrationPageDocument page) => new()
    {
        ["@id"] = page.Id,
        ["@type"] = "catalog:CatalogPage",
        ["parent"] = page.Parent,
        ["count"] = page.Count,
        ["lower"] = page.Lower,
        ["upper"] = page.Upper,
        ["items"] = page.Items.Select(RenderLeaf).ToArray()
    };

    private static Dictionary<string, object?> RenderLeaf(RegistrationLeafDocument leaf)
    {
        var catalogEntry = new Dictionary<string, object?>
        {
            ["@id"] = leaf.Id,
            ["@type"] = "PackageDetails",
            ["id"] = leaf.Package.Id,
            ["version"] = leaf.Package.Version,
            ["authors"] = leaf.Authors,
            ["owners"] = leaf.Owners,
            ["downloads"] = leaf.Downloads,
            ["description"] = leaf.Description,
            ["summary"] = leaf.Summary,
            ["title"] = leaf.Title,
            ["tags"] = leaf.Tags,
            ["projectUrl"] = leaf.ProjectUrl,
            ["readme"] = leaf.Readme,
            ["icon"] = leaf.Icon,
            ["licenseExpression"] = leaf.LicenseExpression,
            ["licenseFile"] = leaf.LicenseFile,
            ["licenseUrl"] = leaf.LicenseUrl,
            ["packageTypes"] = leaf.PackageTypes
                .Select(type => new { name = type.Name, version = type.Version })
                .ToArray(),
            ["repository"] = leaf.Repository is null
                ? null
                : new
                {
                    type = leaf.Repository.Type,
                    url = leaf.Repository.Url,
                    commit = leaf.Repository.Commit,
                    branch = leaf.Repository.Branch
                },
            ["listed"] = leaf.Listed,
            ["published"] = leaf.Published,
            ["dependencyGroups"] = leaf.DependencyGroups
                .Select(group => new
                {
                    targetFramework = group.TargetFramework,
                    dependencies = group.Dependencies
                        .Select(dependency => new
                        {
                            id = dependency.Id,
                            range = dependency.Range
                        })
                        .ToArray()
                })
                .ToArray()
        };
        if (leaf.Deprecation is { } deprecation)
        {
            catalogEntry["deprecation"] = new
            {
                reasons = deprecation.Reasons,
                message = deprecation.Message,
                alternatePackage = deprecation.AlternatePackage is null
                    ? null
                    : new
                    {
                        id = deprecation.AlternatePackage.Id,
                        range = deprecation.AlternatePackage.Range
                    }
            };
        }

        if (leaf.Vulnerabilities.Length > 0)
        {
            catalogEntry["vulnerabilities"] = leaf.Vulnerabilities
                .Select(advisory => new
                {
                    advisoryUrl = advisory.AdvisoryUrl,
                    severity = advisory.Severity
                })
                .ToArray();
        }

        return new Dictionary<string, object?>
        {
            ["@id"] = leaf.Id,
            ["@type"] = "Package",
            ["catalogEntry"] = catalogEntry,
            ["packageContent"] = leaf.PackageContent,
            ["registration"] = leaf.Registration
        };
    }

    private static Dictionary<string, object?> RenderSearchResult(SearchResultDocument result) =>
        new()
        {
            ["@id"] = result.Id,
            ["@type"] = "Package",
            ["registration"] = result.Registration,
            ["id"] = result.Package.Id,
            ["version"] = result.Package.Version,
            ["description"] = result.Description,
            ["summary"] = result.Summary,
            ["title"] = result.Title,
            ["tags"] = result.Tags,
            ["authors"] = result.Authors,
            ["owners"] = result.Owners,
            ["projectUrl"] = result.ProjectUrl,
            ["totalDownloads"] = result.TotalDownloads,
            ["verified"] = result.Verified,
            ["packageTypes"] = result.PackageTypes
                .Select(type => new { name = type.Name, version = type.Version })
                .ToArray(),
            ["versions"] = result.Versions
                .Select(version => new Dictionary<string, object?>
                {
                    ["version"] = version.Version,
                    ["downloads"] = version.Downloads,
                    ["@id"] = version.Id
                })
                .ToArray()
        };

    private static RouteReference RegistrationIndex(string id) =>
        RouteReference.Endpoint(
            "registration.index",
            RouteParameterValue.PackageId("id", id));

    private static RouteReference RegistrationLeaf(string id, string version) =>
        RouteReference.Endpoint(
            "registration.leaf",
            RouteParameterValue.PackageId("id", id),
            RouteParameterValue.PackageVersion("version", version));

    private static bool MatchesBounds(
        IReadOnlyList<CapabilityPackageMetadata> packages,
        string lower,
        string upper)
    {
        if (packages.Count == 0 ||
            !NuGet.Versioning.NuGetVersion.TryParse(lower, out var lowerVersion) ||
            !NuGet.Versioning.NuGetVersion.TryParse(upper, out var upperVersion))
        {
            return false;
        }

        return string.Equals(
                   packages[0].NormalizedVersion,
                   TestPackage.NormalizeVersion(lowerVersion),
                   StringComparison.Ordinal) &&
               string.Equals(
                   packages[^1].NormalizedVersion,
                   TestPackage.NormalizeVersion(upperVersion),
                   StringComparison.Ordinal);
    }
}
