using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.ServiceIndex;

/// <summary>
/// Official service-index operation owner. Resource owners contribute typed metadata;
/// this owner receives the kernel's validated projection and renders it through the
/// transport-neutral operation result, never through an execution context.
/// </summary>
internal sealed class ServiceIndexOperations(
    ImmutableArray<ServiceResourceDescriptor> resources)
{
    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            BuiltInExtensionIds.ServiceIndex,
            OperationOwner.Create<GetServiceIndexRequest, GetServiceIndexResponse>(
                OperationIds.ServiceIndexGet,
                GetAsync));
    }

    private ValueTask<OperationResponse<GetServiceIndexResponse>> GetAsync(
        GetServiceIndexRequest request,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = new GetServiceIndexResponse("3.0.0", resources);
        return ValueTask.FromResult(OperationResponse<GetServiceIndexResponse>.Success(
            response,
            new OperationResult(
                OperationResultStatus.Ok,
                new OperationDocumentBody(new Dictionary<string, object?>
                {
                    ["version"] = response.Version,
                    ["resources"] = response.Resources.Select(CreateDocument).ToArray()
                }))));
    }

    private static Dictionary<string, object?> CreateDocument(ServiceResourceDescriptor resource)
    {
        var document = new Dictionary<string, object?>
        {
            ["@id"] = resource.Route,
            ["@type"] = resource.ResourceType
        };
        if (resource.Comment is not null)
        {
            document["comment"] = resource.Comment;
        }

        return document;
    }
}
