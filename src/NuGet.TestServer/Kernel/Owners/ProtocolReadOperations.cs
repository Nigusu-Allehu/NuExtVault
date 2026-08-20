using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Service-index and flat-container owners. They wrap the existing package store,
/// visibility policy, and content streams without owning any HTTP concern.
/// </summary>
internal sealed class ProtocolReadOperations(
    IPackageStore store,
    IPackageCandidateStore candidates,
    PackageVisibilityPolicy visibility)
{
    private static readonly ImmutableArray<ServiceResourceDescriptor> Resources =
    [
        new(
            "PackageBaseAddress/3.0.0",
            "3.0.0",
            new OperationId(OperationIds.FlatContainerGetVersions),
            "/flatcontainer/"),
        new(
            "RegistrationsBaseUrl/3.6.0",
            "3.6.0",
            new OperationId(OperationIds.RegistrationGetIndex),
            "/registration/"),
        new(
            "SearchQueryService/3.0.0-beta",
            "3.0.0-beta",
            new OperationId(OperationIds.SearchQuery),
            "/query"),
        new(
            "SearchQueryService/3.5.0",
            "3.5.0",
            new OperationId(OperationIds.SearchQuery),
            "/query"),
        new(
            "PackagePublish/2.0.0",
            "2.0.0",
            new OperationId(OperationIds.PackageManagementPush),
            "/package"),
        new(
            "SymbolPackagePublish/4.9.0",
            "4.9.0",
            new OperationId(OperationIds.PackageManagementPushSymbols),
            "/symbolpackage"),
        new(
            "VulnerabilityInfo/6.7.0",
            "6.7.0",
            new OperationId(OperationIds.VulnerabilitiesGetIndex),
            "/v3/vulnerabilities/index.json")
    ];

    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetServiceIndexRequest, GetServiceIndexResponse>(
                OperationIds.ServiceIndexGet,
                GetServiceIndexAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetPackageVersionsRequest, GetPackageVersionsResponse>(
                OperationIds.FlatContainerGetVersions,
                GetVersionsAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetPackageRequest, GetPackageResponse>(
                OperationIds.FlatContainerGetPackage,
                GetPackageAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetNuspecRequest, GetNuspecResponse>(
                OperationIds.FlatContainerGetNuspec,
                GetNuspecAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetPackageHashRequest, GetPackageHashResponse>(
                OperationIds.FlatContainerGetHash,
                GetHashAsync));
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<GetSymbolRequest, GetSymbolResponse>(
                OperationIds.FlatContainerGetSymbol,
                GetSymbolAsync));
    }

    private ValueTask<OperationResponse<GetServiceIndexResponse>> GetServiceIndexAsync(
        GetServiceIndexRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = new GetServiceIndexResponse("3.0.0", Resources);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(new Dictionary<string, object?>
            {
                ["version"] = response.Version,
                ["resources"] = response.Resources
                    .Select(resource => new Dictionary<string, string>
                    {
                        ["@id"] = $"{request.BaseAddress}{resource.RouteName}",
                        ["@type"] = resource.ResourceType
                    })
                    .ToArray()
            })));
        return ValueTask.FromResult(OperationResponse<GetServiceIndexResponse>.Success(response));
    }

    private async ValueTask<OperationResponse<GetPackageVersionsResponse>> GetVersionsAsync(
        GetPackageVersionsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var packages = (await candidates.FindStoredByIdAsync(request.PackageId, token))
            .Where(package => visibility.CanRead(
                package,
                PackageResourceClass.VersionEnumeration))
            .ToArray();
        if (packages.Length == 0)
        {
            return OperationResponse<GetPackageVersionsResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.PackageId}' has no readable versions."));
        }

        var response = new GetPackageVersionsResponse(
            [.. packages.Select(package => package.NormalizedVersion)]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(new { versions = response.Versions })));
        return OperationResponse<GetPackageVersionsResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetPackageResponse>> GetPackageAsync(
        GetPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var package = await FindReadableAsync(
            request.Package,
            PackageResourceClass.ExactContent,
            token);
        if (package is null)
        {
            return OperationResponse<GetPackageResponse>.Failure(NotFound(request.Package));
        }

        Stream content;
        try
        {
            content = package.OpenReadStream();
        }
        catch (FileNotFoundException)
        {
            return OperationResponse<GetPackageResponse>.Failure(NotFound(request.Package));
        }

        var handle = context.Content.RegisterStream(
            content,
            "application/octet-stream",
            package.ContentLength,
            supportsRanges: true);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new ContentResponseBody(handle)));
        return OperationResponse<GetPackageResponse>.Success(
            new GetPackageResponse(
                new ContentDescriptor(
                    handle,
                    package.PackageHash,
                    package.ContentLength,
                    SupportsRanges: true)));
    }

    private async ValueTask<OperationResponse<GetNuspecResponse>> GetNuspecAsync(
        GetNuspecRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var package = await FindReadableAsync(
            request.Package,
            PackageResourceClass.ExactContent,
            token);
        if (package is null)
        {
            return OperationResponse<GetNuspecResponse>.Failure(NotFound(request.Package));
        }

        var handle = context.Content.RegisterBytes(
            package.NuspecContent,
            "text/xml; charset=utf-8");
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new ContentResponseBody(handle)));
        return OperationResponse<GetNuspecResponse>.Success(
            new GetNuspecResponse(
                new ContentDescriptor(
                    handle,
                    null,
                    package.NuspecContent.Length,
                    SupportsRanges: false)));
    }

    private async ValueTask<OperationResponse<GetPackageHashResponse>> GetHashAsync(
        GetPackageHashRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var package = await FindReadableAsync(
            request.Package,
            PackageResourceClass.ExactContent,
            token);
        if (package is null)
        {
            return OperationResponse<GetPackageHashResponse>.Failure(NotFound(request.Package));
        }

        var response = new GetPackageHashResponse(package.PackageHash);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new TextResponseBody(response.Sha512, "text/plain; charset=utf-8")));
        return OperationResponse<GetPackageHashResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetSymbolResponse>> GetSymbolAsync(
        GetSymbolRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var symbols = await store.FindSymbolAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        if (symbols is null)
        {
            return OperationResponse<GetSymbolResponse>.Failure(NotFound(request.Package));
        }

        var handle = context.Content.RegisterBytes(symbols, "application/octet-stream");
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new ContentResponseBody(handle)));
        return OperationResponse<GetSymbolResponse>.Success(
            new GetSymbolResponse(
                new ContentDescriptor(handle, null, symbols.Length, SupportsRanges: false)));
    }

    private async ValueTask<TestPackage?> FindReadableAsync(
        PackageIdentity identity,
        PackageResourceClass resourceClass,
        CancellationToken token)
    {
        var package = await store.FindAsync(identity.Id, identity.Version, token);
        return package is not null && visibility.CanRead(package, resourceClass)
            ? package
            : null;
    }

    private static OperationError NotFound(PackageIdentity identity) =>
        OperationErrorPolicy.NotFound(
            $"Package '{identity.Id}' version '{identity.Version}' is not readable.");
}
