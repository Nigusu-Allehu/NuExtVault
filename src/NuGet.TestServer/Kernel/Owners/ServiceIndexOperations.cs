using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Official service-index operation owner. Resource owners contribute typed metadata;
/// this owner can only request the kernel's validated projection.
/// </summary>
internal sealed class ServiceIndexOperations(ServiceIndexResourceRegistry resources)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.ServiceIndex,
            new DelegateOperationOwner<GetServiceIndexRequest, GetServiceIndexResponse>(
                OperationIds.ServiceIndexGet,
                GetAsync));
    }

    private ValueTask<OperationResponse<GetServiceIndexResponse>> GetAsync(
        GetServiceIndexRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = new GetServiceIndexResponse("3.0.0", resources.Resources);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(new Dictionary<string, object?>
            {
                ["version"] = response.Version,
                ["resources"] = response.Resources.Select(CreateDocument).ToArray()
            })));
        return ValueTask.FromResult(OperationResponse<GetServiceIndexResponse>.Success(response));
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
