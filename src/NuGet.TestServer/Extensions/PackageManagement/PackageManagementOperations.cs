using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Extensions.PackageManagement;

internal sealed class PackageManagementOperations(
    IPackagePushCapability push,
    IPackageSymbolsPushCapability symbols,
    IPackageManagementListCapability packages,
    IPackageUnlistCapability unlist,
    IPackageRelistCapability relist,
    IPackageDeleteCapability delete)
{
    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            PackageManagementModule.ExtensionId,
            OperationOwner.Create<PushPackageRequest, PushPackageResponse>(
                OperationIds.PackageManagementPush,
                PushAsync));
        registry.Register(
            PackageManagementModule.ExtensionId,
            OperationOwner.Create<PushSymbolsRequest, PushSymbolsResponse>(
                OperationIds.PackageManagementPushSymbols,
                PushSymbolsAsync));
        registry.Register(
            PackageManagementModule.ExtensionId,
            OperationOwner.Create<ListPackagesRequest, ListPackagesResponse>(
                OperationIds.PackageManagementList,
                ListAsync));
        registry.Register(
            PackageManagementModule.ExtensionId,
            OperationOwner.Create<UnlistPackageRequest, UnlistPackageResponse>(
                OperationIds.PackageManagementUnlist,
                UnlistAsync));
        registry.Register(
            PackageManagementModule.ExtensionId,
            OperationOwner.Create<RelistPackageRequest, RelistPackageResponse>(
                OperationIds.PackageManagementRelist,
                RelistAsync));
        registry.Register(
            PackageManagementModule.ExtensionId,
            OperationOwner.Create<DeletePackageRequest, DeletePackageResponse>(
                OperationIds.PackageManagementDelete,
                DeleteAsync));
    }

    private async ValueTask<OperationResponse<PushPackageResponse>> PushAsync(
        PushPackageRequest request,
        CancellationToken token)
    {
        var publication = await push.PublishAsync(request.Content, token);
        if (publication.Outcome == PublicationOutcome.Unauthorized)
        {
            return OperationResponse<PushPackageResponse>.Failure(
                OperationErrors.PolicyDenied(publication.Message));
        }

        return OperationResponse<PushPackageResponse>.Success(
            new PushPackageResponse(publication.Package, publication.Outcome),
            RenderPublication(publication));
    }

    private async ValueTask<OperationResponse<PushSymbolsResponse>> PushSymbolsAsync(
        PushSymbolsRequest request,
        CancellationToken token)
    {
        var identity = await symbols.StoreAsync(request.Content, token);
        return OperationResponse<PushSymbolsResponse>.Success(
            new PushSymbolsResponse(identity),
            new OperationResult(
                OperationResultStatus.Created,
                new OperationDocumentBody(new { id = identity.Id, version = identity.Version }),
                $"/__test/packages/{Uri.EscapeDataString(identity.Id)}/{identity.Version}/symbols"));
    }

    private async ValueTask<OperationResponse<ListPackagesResponse>> ListAsync(
        ListPackagesRequest request,
        CancellationToken token)
    {
        var summaries = await packages.QueryAsync(
            request.PackageId,
            request.Skip,
            request.Take,
            token);
        var response = new ListPackagesResponse(summaries);
        return OperationResponse<ListPackagesResponse>.Success(
            response,
            OperationResult.Ok(response.Packages.Select(package => new
            {
                id = package.Package.Id,
                version = package.Package.Version,
                listed = package.Listed,
                published = package.Published
            }).ToArray()));
    }

    private async ValueTask<OperationResponse<UnlistPackageResponse>> UnlistAsync(
        UnlistPackageRequest request,
        CancellationToken token)
    {
        var mutation = await unlist.SetUnlistedAsync(request.Package, token);
        return mutation.Outcome switch
        {
            PackageMutationOutcome.Succeeded =>
                OperationResponse<UnlistPackageResponse>.Success(
                    new UnlistPackageResponse(request.Package),
                    OperationResult.NoContent()),
            PackageMutationOutcome.Forbidden =>
                OperationResponse<UnlistPackageResponse>.Failure(
                    OperationErrors.PolicyDenied(mutation.Detail!)),
            _ => OperationResponse<UnlistPackageResponse>.Failure(NotFound(request.Package))
        };
    }

    private async ValueTask<OperationResponse<RelistPackageResponse>> RelistAsync(
        RelistPackageRequest request,
        CancellationToken token)
    {
        var mutation = await relist.SetListedAsync(request.Package, token);
        return mutation.Outcome switch
        {
            PackageMutationOutcome.Succeeded =>
                OperationResponse<RelistPackageResponse>.Success(
                    new RelistPackageResponse(request.Package),
                    OperationResult.NoContent()),
            PackageMutationOutcome.Forbidden =>
                OperationResponse<RelistPackageResponse>.Failure(
                    OperationErrors.PolicyDenied(mutation.Detail!)),
            _ => OperationResponse<RelistPackageResponse>.Failure(NotFound(request.Package))
        };
    }

    private async ValueTask<OperationResponse<DeletePackageResponse>> DeleteAsync(
        DeletePackageRequest request,
        CancellationToken token)
    {
        var mutation = await delete.DeleteAsync(
            request.Package,
            request.Reason,
            token);
        return mutation.Outcome switch
        {
            PackageMutationOutcome.Succeeded =>
                OperationResponse<DeletePackageResponse>.Success(
                    new DeletePackageResponse(request.Package),
                    OperationResult.NoContent()),
            PackageMutationOutcome.Forbidden =>
                OperationResponse<DeletePackageResponse>.Failure(
                    OperationErrors.PolicyDenied(mutation.Detail!)),
            _ => OperationResponse<DeletePackageResponse>.Failure(NotFound(request.Package))
        };
    }

    private static OperationResult RenderPublication(PackagePublicationDocument publication) =>
        publication.Outcome switch
        {
            PublicationOutcome.Published => new OperationResult(
                OperationResultStatus.Created,
                Body(publication),
                "/package"),
            PublicationOutcome.Duplicate => new OperationResult(
                OperationResultStatus.Ok,
                Body(publication)),
            PublicationOutcome.Quarantined => new OperationResult(
                OperationResultStatus.Accepted,
                Body(publication),
                "/package"),
            PublicationOutcome.Rejected => new OperationResult(
                OperationResultStatus.Unprocessable,
                Body(publication)),
            PublicationOutcome.QuotaExceeded => new OperationResult(
                OperationResultStatus.TooManyRequests),
            _ => new OperationResult(
                OperationResultStatus.Conflict,
                Body(publication))
        };

    private static OperationDocumentBody Body(PackagePublicationDocument publication) =>
        new(new { outcome = publication.Outcome, message = publication.Message });

    private static OperationError NotFound(PackageIdentity package) =>
        OperationErrors.NotFound(
            $"Package '{package.Id}' version '{package.Version}' does not exist.");
}
