using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Registration;

internal sealed class RegistrationOperations(
    IRegistrationMetadataReadCapability packages,
    IRegistrationVulnerabilityReadCapability vulnerabilities,
    IDocumentContributionSource contributions)
{
    private readonly RegistrationDocumentBuilder _documents =
        new(vulnerabilities, contributions);

    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            RegistrationModule.ExtensionId,
            OperationOwner.Create<GetRegistrationIndexRequest, GetRegistrationIndexResponse>(
                OperationIds.RegistrationGetIndex,
                GetIndexAsync));
        registry.Register(
            RegistrationModule.ExtensionId,
            OperationOwner.Create<GetRegistrationPageRequest, GetRegistrationPageResponse>(
                OperationIds.RegistrationGetPage,
                GetPageAsync));
        registry.Register(
            RegistrationModule.ExtensionId,
            OperationOwner.Create<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
                OperationIds.RegistrationGetLeaf,
                GetLeafAsync));
    }

    private async ValueTask<OperationResponse<GetRegistrationIndexResponse>> GetIndexAsync(
        GetRegistrationIndexRequest request,
        CancellationToken token)
    {
        var found = await packages.FindByIdAsync(request.PackageId, token);
        if (found.Length == 0)
        {
            return OperationResponse<GetRegistrationIndexResponse>.Failure(
                OperationErrors.NotFound(
                    $"Package '{request.PackageId}' has no registration."));
        }

        var response = new GetRegistrationIndexResponse(
            RegistrationDocumentBuilder.CreateIndexReference(
                found[0].Package.Id.ToLowerInvariant()),
            1,
            [await _documents.CreatePageAsync(found, token)]);
        return OperationResponse<GetRegistrationIndexResponse>.Success(
            response,
            OperationResult.Ok(new Dictionary<string, object?>
            {
                ["@id"] = response.Id,
                ["count"] = response.Count,
                ["items"] = response.Items.Select(RegistrationDocumentRenderer.RenderPage).ToArray()
            }));
    }

    private async ValueTask<OperationResponse<GetRegistrationPageResponse>> GetPageAsync(
        GetRegistrationPageRequest request,
        CancellationToken token)
    {
        var found = await packages.FindByIdAsync(request.PackageId, token);
        if (!RegistrationPageBounds.Matches(
                found.Select(package => package.Package.Version).ToArray(),
                request.Lower,
                request.Upper))
        {
            return OperationResponse<GetRegistrationPageResponse>.Failure(
                OperationErrors.NotFound(
                    $"Registration page '{request.Lower}'-'{request.Upper}' does not exist."));
        }

        var response = new GetRegistrationPageResponse(
            await _documents.CreatePageAsync(found, token));
        return OperationResponse<GetRegistrationPageResponse>.Success(
            response,
            OperationResult.Ok(RegistrationDocumentRenderer.RenderPage(response.Page)));
    }

    private async ValueTask<OperationResponse<GetRegistrationLeafResponse>> GetLeafAsync(
        GetRegistrationLeafRequest request,
        CancellationToken token)
    {
        var package = await packages.FindLeafAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        if (package is null)
        {
            return OperationResponse<GetRegistrationLeafResponse>.Failure(
                OperationErrors.NotFound(
                    $"Package '{request.Package.Id}' version " +
                    $"'{request.Package.Version}' has no registration."));
        }

        var response = new GetRegistrationLeafResponse(
            await _documents.CreateLeafAsync(package, token));
        return OperationResponse<GetRegistrationLeafResponse>.Success(
            response,
            OperationResult.Ok(RegistrationDocumentRenderer.RenderLeaf(response.Leaf)));
    }
}
