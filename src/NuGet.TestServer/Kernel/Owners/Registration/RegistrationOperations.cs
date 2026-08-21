using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Kernel.Owners.Registration;

internal sealed class RegistrationOperations
{
    private readonly IRegistrationPackageQuery _packages;
    private readonly RegistrationDocumentBuilder _documents;

    public RegistrationOperations(
        IRegistrationPackageQuery packages,
        RegistrationDocumentBuilder documents)
    {
        _packages = packages;
        _documents = documents;
    }

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
    }

    private async ValueTask<OperationResponse<GetRegistrationIndexResponse>> GetIndexAsync(
        GetRegistrationIndexRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var packages = await _packages.FindByIdAsync(request.PackageId, token);
        if (packages.Count == 0)
        {
            return OperationResponse<GetRegistrationIndexResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.PackageId}' has no registration."));
        }

        var response = new GetRegistrationIndexResponse(
            RegistrationDocumentBuilder.CreateIndexReference(packages[0].Id.ToLowerInvariant()),
            1,
            [_documents.CreatePage(packages)]);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(new Dictionary<string, object?>
            {
                ["@id"] = response.Id,
                ["count"] = response.Count,
                ["items"] = response.Items.Select(RegistrationDocumentRenderer.RenderPage).ToArray()
            })));
        return OperationResponse<GetRegistrationIndexResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetRegistrationPageResponse>> GetPageAsync(
        GetRegistrationPageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var packages = await _packages.FindByIdAsync(request.PackageId, token);
        if (!RegistrationPageBounds.Matches(
                packages.Select(package => package.NormalizedVersion).ToArray(),
                request.Lower,
                request.Upper))
        {
            return OperationResponse<GetRegistrationPageResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Registration page '{request.Lower}'-'{request.Upper}' does not exist."));
        }

        var response = new GetRegistrationPageResponse(_documents.CreatePage(packages));
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(RegistrationDocumentRenderer.RenderPage(response.Page))));
        return OperationResponse<GetRegistrationPageResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetRegistrationLeafResponse>> GetLeafAsync(
        GetRegistrationLeafRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var package = await _packages.FindLeafAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        if (package is null)
        {
            return OperationResponse<GetRegistrationLeafResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.Package.Id}' version " +
                    $"'{request.Package.Version}' has no registration."));
        }

        var response = new GetRegistrationLeafResponse(_documents.CreateLeaf(package));
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(RegistrationDocumentRenderer.RenderLeaf(response.Leaf))));
        return OperationResponse<GetRegistrationLeafResponse>.Success(response);
    }
}
