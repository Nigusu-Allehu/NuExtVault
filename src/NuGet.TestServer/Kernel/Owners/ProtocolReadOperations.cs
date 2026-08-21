using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Service-index and flat-container owners. They wrap the existing package store,
/// visibility policy, and content streams without owning any HTTP concern.
/// </summary>
internal sealed class ProtocolReadOperations(IPackageReadCapability packages)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
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

    private async ValueTask<OperationResponse<GetPackageVersionsResponse>> GetVersionsAsync(
        GetPackageVersionsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var readablePackages = await packages.FindReadableStoredByIdAsync(
            request.PackageId,
            PackageResourceClass.VersionEnumeration,
            token);
        if (readablePackages.Count == 0)
        {
            return OperationResponse<GetPackageVersionsResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.PackageId}' has no readable versions."));
        }

        var response = new GetPackageVersionsResponse(
            [.. readablePackages.Select(package => package.NormalizedVersion)]);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(new { versions = response.Versions })));
        return OperationResponse<GetPackageVersionsResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetPackageResponse>> GetPackageAsync(
        GetPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        CapabilityPackageContent? content;
        try
        {
            content = await packages.OpenContentAsync(
                request.Package.Id,
                request.Package.Version,
                PackageResourceClass.ExactContent,
                token);
        }
        catch (FileNotFoundException)
        {
            content = null;
        }

        if (content is null)
        {
            return OperationResponse<GetPackageResponse>.Failure(NotFound(request.Package));
        }

        var handle = context.Content.RegisterStream(
            content.Stream,
            "application/octet-stream",
            content.Length,
            supportsRanges: true);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationContentBody(handle)));
        return OperationResponse<GetPackageResponse>.Success(
            new GetPackageResponse(
                new ContentDescriptor(
                    handle,
                    content.Sha512,
                    content.Length,
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
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationContentBody(handle)));
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
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationTextBody(response.Sha512, "text/plain; charset=utf-8")));
        return OperationResponse<GetPackageHashResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetSymbolResponse>> GetSymbolAsync(
        GetSymbolRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var symbols = await packages.FindSymbolAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        if (symbols is null)
        {
            return OperationResponse<GetSymbolResponse>.Failure(NotFound(request.Package));
        }

        var handle = context.Content.RegisterBytes(symbols, "application/octet-stream");
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationContentBody(handle)));
        return OperationResponse<GetSymbolResponse>.Success(
            new GetSymbolResponse(
                new ContentDescriptor(handle, null, symbols.Length, SupportsRanges: false)));
    }

    private ValueTask<CapabilityPackageMetadata?> FindReadableAsync(
        PackageIdentity identity,
        PackageResourceClass resourceClass,
        CancellationToken token) =>
        packages.FindReadableAsync(identity.Id, identity.Version, resourceClass, token);

    private static OperationError NotFound(PackageIdentity identity) =>
        OperationErrorPolicy.NotFound(
            $"Package '{identity.Id}' version '{identity.Version}' is not readable.");
}
