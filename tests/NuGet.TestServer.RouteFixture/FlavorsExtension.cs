using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.RouteFixture;

/// <summary>
/// Step 11A closed-world route proof. This assembly is compiled separately from the
/// kernel and contributes <c>/flavors/index.json</c> using only the descriptor,
/// binder, and operation-owner seams. It never sees <c>WebApplication</c>,
/// <c>IEndpointRouteBuilder</c>, the root service provider, or an
/// <c>HttpContext</c>.
/// </summary>
internal static class FlavorsExtension
{
    public const string ExtensionId = "test.flavors";
    public const string GetIndexOperationId = "Test.Flavors.GetIndex";

    private static EndpointDescriptor Descriptor { get; } = new()
    {
        Name = "test.flavors.index",
        Methods = ["GET", "HEAD"],
        PathTemplate = "/flavors/index.json",
        Head = EndpointHeadPolicy.MirrorsGet,
        Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
        Body = EndpointBodyBinding.None,
        Limits = EndpointLimits.BodyFree,
        Operations =
        [
            EndpointDescriptor.Operation<GetFlavorIndexRequest, GetFlavorIndexResponse>(
                GetIndexOperationId)
        ],
        Handler = EndpointHandler.Create((request, _) => ValueTask.FromResult(
            EndpointInvocation.Operation<GetFlavorIndexRequest, GetFlavorIndexResponse>(
                GetIndexOperationId,
                new GetFlavorIndexRequest(request.GetQuery("filter")))))
    };
    public static ExtensionContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [GetIndexOperationId],
            [Descriptor],
            [],
            []),
        [
            new OperationBinding(
                new OperationContract(
                    new OperationId(GetIndexOperationId),
                    OperationFamily.Diagnostics,
                    1,
                    $"{nameof(GetFlavorIndexRequest)}.v1",
                    $"{nameof(GetFlavorIndexResponse)}.v1"),
                typeof(GetFlavorIndexRequest),
                typeof(GetFlavorIndexResponse))
        ],
        builder => builder.Register(ExtensionId, new FlavorIndexOwner()));

}

internal sealed record GetFlavorIndexRequest(string? Filter);

internal sealed record GetFlavorIndexResponse(ImmutableArray<string> Flavors);

internal sealed class FlavorIndexOwner : IOperationOwner<GetFlavorIndexRequest, GetFlavorIndexResponse>
{
    private static readonly ImmutableArray<string> Flavors = ["salty", "sweet", "umami"];

    public OperationId OperationId { get; } = new(FlavorsExtension.GetIndexOperationId);

    public ValueTask<OperationResponse<GetFlavorIndexResponse>> HandleAsync(
        GetFlavorIndexRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var flavors = request.Filter is null
            ? Flavors
            : [.. Flavors.Where(flavor => flavor.StartsWith(request.Filter, StringComparison.Ordinal))];
        return ValueTask.FromResult(
            OperationResponse<GetFlavorIndexResponse>.Success(new GetFlavorIndexResponse(flavors)));
    }
}
