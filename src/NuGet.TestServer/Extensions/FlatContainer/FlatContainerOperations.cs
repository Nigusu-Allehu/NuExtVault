using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Extensions.FlatContainer;

/// <summary>
/// Flat-container and symbol read owners. They read authoritative package state through
/// narrow capabilities and describe responses with the transport-neutral rendering
/// contract; they never touch a package store, an execution context, a content stream,
/// or HTTP.
/// </summary>
internal sealed class FlatContainerOperations(
    IPackageMetadataReadCapability metadata,
    IPackageContentReadCapability content,
    IPackageSymbolReadCapability symbols)
{
    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            FlatContainerModule.ExtensionId,
            OperationOwner.Create<GetPackageVersionsRequest, GetPackageVersionsResponse>(
                OperationIds.FlatContainerGetVersions,
                GetVersionsAsync));
        registry.Register(
            FlatContainerModule.ExtensionId,
            OperationOwner.Create<GetPackageRequest, GetPackageResponse>(
                OperationIds.FlatContainerGetPackage,
                GetPackageAsync));
        registry.Register(
            FlatContainerModule.ExtensionId,
            OperationOwner.Create<GetNuspecRequest, GetNuspecResponse>(
                OperationIds.FlatContainerGetNuspec,
                GetNuspecAsync));
        registry.Register(
            FlatContainerModule.ExtensionId,
            OperationOwner.Create<GetPackageHashRequest, GetPackageHashResponse>(
                OperationIds.FlatContainerGetHash,
                GetHashAsync));
        registry.Register(
            FlatContainerModule.ExtensionId,
            OperationOwner.Create<GetSymbolRequest, GetSymbolResponse>(
                OperationIds.FlatContainerGetSymbol,
                GetSymbolAsync));
    }

    private async ValueTask<OperationResponse<GetPackageVersionsResponse>> GetVersionsAsync(
        GetPackageVersionsRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var versions = await metadata.GetReadableVersionsAsync(request.PackageId, token);
        if (versions.Length == 0)
        {
            return OperationResponse<GetPackageVersionsResponse>.Failure(
                OperationErrors.NotFound(
                    $"Package '{request.PackageId}' has no readable versions."));
        }

        var response = new GetPackageVersionsResponse(versions);
        return OperationResponse<GetPackageVersionsResponse>.Success(
            response,
            OperationResult.Ok(new { versions = response.Versions }));
    }

    private async ValueTask<OperationResponse<GetPackageResponse>> GetPackageAsync(
        GetPackageRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var package = await content.OpenPackageAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        return package is null
            ? OperationResponse<GetPackageResponse>.Failure(NotFound(request.Package))
            : OperationResponse<GetPackageResponse>.Success(
                new GetPackageResponse(package),
                OperationResult.Ok(package.Content));
    }

    private async ValueTask<OperationResponse<GetNuspecResponse>> GetNuspecAsync(
        GetNuspecRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nuspec = await metadata.OpenNuspecAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        return nuspec is null
            ? OperationResponse<GetNuspecResponse>.Failure(NotFound(request.Package))
            : OperationResponse<GetNuspecResponse>.Success(
                new GetNuspecResponse(nuspec),
                OperationResult.Ok(nuspec.Content));
    }

    private async ValueTask<OperationResponse<GetPackageHashResponse>> GetHashAsync(
        GetPackageHashRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = await metadata.GetPackageHashAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        return hash is null
            ? OperationResponse<GetPackageHashResponse>.Failure(NotFound(request.Package))
            : OperationResponse<GetPackageHashResponse>.Success(
                new GetPackageHashResponse(hash),
                OperationResult.Text(hash, "text/plain; charset=utf-8"));
    }

    private async ValueTask<OperationResponse<GetSymbolResponse>> GetSymbolAsync(
        GetSymbolRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var package = await symbols.OpenSymbolsAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        return package is null
            ? OperationResponse<GetSymbolResponse>.Failure(NotFound(request.Package))
            : OperationResponse<GetSymbolResponse>.Success(
                new GetSymbolResponse(package),
                OperationResult.Ok(package.Content));
    }

    private static OperationError NotFound(PackageIdentity identity) =>
        OperationErrors.NotFound(
            $"Package '{identity.Id}' version '{identity.Version}' is not readable.");
}
