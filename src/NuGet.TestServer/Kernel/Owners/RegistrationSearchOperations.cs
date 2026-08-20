using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Registration and search owners. Documents are produced as typed contracts first
/// and then rendered into the current protocol shape.
/// </summary>
internal sealed class RegistrationSearchOperations(
    IPackageStore store,
    IPackageCandidateStore candidates,
    PackageVisibilityPolicy visibility,
    VulnerabilitySnapshotProvider vulnerabilities)
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

        var normalizedId = packages[0].Identity.Id.ToLowerInvariant();
        var response = new GetRegistrationIndexResponse(
            $"{request.BaseAddress}/registration/{normalizedId}/index.json",
            1,
            [CreatePage(packages, request.BaseAddress)]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(new Dictionary<string, object?>
            {
                ["@id"] = response.IdUrl,
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
        if (!RegistrationPageBounds.Matches(packages, request.Lower, request.Upper))
        {
            return OperationResponse<GetRegistrationPageResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Registration page '{request.Lower}'-'{request.Upper}' does not exist."));
        }

        var response = new GetRegistrationPageResponse(CreatePage(packages, request.BaseAddress));
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(RenderPage(response.Page))));
        return OperationResponse<GetRegistrationPageResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetRegistrationLeafResponse>> GetLeafAsync(
        GetRegistrationLeafRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var package = await store.FindStoredAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        if (package is null || !visibility.CanRead(package, PackageResourceClass.Registration))
        {
            return OperationResponse<GetRegistrationLeafResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.Package.Id}' version " +
                    $"'{request.Package.Version}' has no registration."));
        }

        var response = new GetRegistrationLeafResponse(
            CreateLeaf(package, request.BaseAddress));
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(RenderLeaf(response.Leaf))));
        return OperationResponse<GetRegistrationLeafResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<SearchResponse>> SearchAsync(
        SearchRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var page = await store.SearchAsync(
            request.Query,
            request.IncludePrerelease,
            request.Skip,
            request.Take,
            token,
            request.PackageType);
        var response = new SearchResponse(
            page.TotalHits,
            [
                .. page.Items.Select(item => CreateSearchResult(
                    item.Package,
                    item.Versions,
                    request.BaseAddress))
            ]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(new
            {
                totalHits = response.TotalHits,
                data = response.Data.Select(RenderSearchResult).ToArray()
            })));
        return OperationResponse<SearchResponse>.Success(response);
    }

    private async ValueTask<TestPackage[]> FindRegistrationCandidatesAsync(
        string packageId,
        CancellationToken token) =>
        [
            .. (await candidates.FindStoredByIdAsync(packageId, token))
                .Where(package => visibility.CanRead(package, PackageResourceClass.Registration))
        ];

    private RegistrationPageDocument CreatePage(
        IReadOnlyList<TestPackage> packages,
        string baseAddress)
    {
        var first = packages[0];
        var last = packages[^1];
        var normalizedId = first.Identity.Id.ToLowerInvariant();
        return new RegistrationPageDocument(
            $"{baseAddress}/registration/{normalizedId}/page/" +
            $"{first.NormalizedVersion}/{last.NormalizedVersion}.json",
            $"{baseAddress}/registration/{normalizedId}/index.json",
            packages.Count,
            first.NormalizedVersion,
            last.NormalizedVersion,
            [.. packages.Select(package => CreateLeaf(package, baseAddress))]);
    }

    private RegistrationLeafDocument CreateLeaf(TestPackage package, string baseAddress)
    {
        var id = package.Identity.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        var advisories = vulnerabilities.Active.Find(
            package.Identity.Id,
            package.Identity.Version);
        return new RegistrationLeafDocument(
            $"{baseAddress}/registration/{id}/{version}.json",
            $"{baseAddress}/registration/{id}/index.json",
            $"{baseAddress}/flatcontainer/{id}/{version}/{id}.{version}.nupkg",
            new PackageIdentity(package.Identity.Id, version),
            package.Authors,
            [.. package.RepositoryMetadata.Owners],
            package.RepositoryMetadata.Downloads,
            package.Description,
            package.Summary,
            string.IsNullOrEmpty(package.Title) ? package.Identity.Id : package.Title,
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
        TestPackage package,
        IReadOnlyList<TestPackage> versions,
        string baseAddress)
    {
        var id = package.Identity.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        return new SearchResultDocument(
            $"{baseAddress}/registration/{id}/{version}.json",
            $"{baseAddress}/registration/{id}/index.json",
            new PackageIdentity(package.Identity.Id, version),
            package.Description,
            string.IsNullOrEmpty(package.Summary) ? package.Description : package.Summary,
            string.IsNullOrEmpty(package.Title) ? package.Identity.Id : package.Title,
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
                    item.RepositoryMetadata.Downloads))
            ]);
    }

    private static Dictionary<string, object?> RenderPage(RegistrationPageDocument page) => new()
    {
        ["@id"] = page.IdUrl,
        ["@type"] = "catalog:CatalogPage",
        ["parent"] = page.ParentUrl,
        ["count"] = page.Count,
        ["lower"] = page.Lower,
        ["upper"] = page.Upper,
        ["items"] = page.Items.Select(RenderLeaf).ToArray()
    };

    private static Dictionary<string, object?> RenderLeaf(RegistrationLeafDocument leaf)
    {
        var catalogEntry = new Dictionary<string, object?>
        {
            ["@id"] = leaf.IdUrl,
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
            ["@id"] = leaf.IdUrl,
            ["@type"] = "Package",
            ["catalogEntry"] = catalogEntry,
            ["packageContent"] = leaf.PackageContentUrl,
            ["registration"] = leaf.RegistrationUrl
        };
    }

    private static Dictionary<string, object?> RenderSearchResult(SearchResultDocument result) =>
        new()
        {
            ["@id"] = result.IdUrl,
            ["@type"] = "Package",
            ["registration"] = result.RegistrationUrl,
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
                    ["@id"] = $"{RegistrationBaseUrl(result.RegistrationUrl)}/{version.Version}.json"
                })
                .ToArray()
        };

    private static string RegistrationBaseUrl(string registrationUrl) =>
        registrationUrl.EndsWith("/index.json", StringComparison.Ordinal)
            ? registrationUrl[..^"/index.json".Length]
            : registrationUrl;
}
