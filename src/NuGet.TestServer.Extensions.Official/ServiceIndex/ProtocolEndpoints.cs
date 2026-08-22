using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.ServiceIndex;

/// <summary>
/// NuGet protocol endpoint descriptors. They declare the transport surface only; the
/// kernel generates and freezes the routes and dispatches the declared operations.
/// </summary>
internal static class ProtocolEndpoints
{
    public static ImmutableArray<EndpointDescriptor> ServiceIndex { get; } =
    [
        new()
        {
            Name = "service-index.get",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/v3/index.json",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetServiceIndexRequest, GetServiceIndexResponse>(
                    OperationIds.ServiceIndexGet)
            ],
            Handler = EndpointHandler.Create<GetServiceIndexRequest, GetServiceIndexResponse>(
                OperationIds.ServiceIndexGet,
                _ => new GetServiceIndexRequest())
        }
    ];

}
