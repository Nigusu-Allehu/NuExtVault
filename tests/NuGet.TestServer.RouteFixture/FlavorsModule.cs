using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.RouteFixture;

/// <summary>
/// Step 11C conformance module. This assembly is compiled separately from the kernel
/// and references only <c>NuGet.TestServer.Extensions.Abstractions</c>. It contributes
/// its identity, one typed operation, one route, one service-index resource, and one
/// requested capability. It never sees <c>WebApplication</c>,
/// <c>IEndpointRouteBuilder</c>, the root service provider, an <c>HttpContext</c>, the
/// kernel operation registry, or any official extension implementation.
/// </summary>
internal sealed class FlavorsModule : IExtensionModule
{
    public const string ExtensionId = "contoso.flavors";
    public const string GetIndexOperationId = "Contoso.Flavors.GetIndex";
    public const string RouteName = "contoso.flavors.index";
    public const string ResourceType = "Flavors";
    public const string ResourceVersion = "1.0.0";

    private static readonly OperationFamily Family = OperationFamily.Custom("Contoso.Flavors");

    private static EndpointDescriptor Descriptor { get; } = new()
    {
        Name = RouteName,
        Methods = ["GET", "HEAD"],
        PathTemplate = "/flavors/index.json",
        Head = EndpointHeadPolicy.MirrorsGet,
        Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
        Body = EndpointBodyBinding.None,
        Limits = EndpointLimits.BodyFree,
        QueryParameters = [new EndpointParameter("filter", IsRequired: false)],
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

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [GetIndexOperationId],
            [Descriptor],
            [
                new ServiceResourceContribution(
                    ResourceType,
                    ResourceVersion,
                    new OperationId(GetIndexOperationId),
                    "/flavors/index.json",
                    ServiceResourceVisibility.Advertised,
                    ServiceResourceAccess.Read,
                    [],
                    [],
                    "Contoso flavor catalog.",
                    1000,
                    ServiceResourceReadiness.Ready)
            ],
            [new CapabilityRequest(KernelCapabilityNames.HostClockRead, IsRequired: true)]),
        [
            new OperationBinding(
                new OperationContract(
                    new OperationId(GetIndexOperationId),
                    Family,
                    1,
                    $"{nameof(GetFlavorIndexRequest)}.v1",
                    $"{nameof(GetFlavorIndexResponse)}.v1"),
                typeof(GetFlavorIndexRequest),
                typeof(GetFlavorIndexResponse))
        ]);

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        registry.Register(
            ExtensionId,
            new FlavorIndexOwner(capabilities.GetRequired<IHostClockCapability>(
                KernelCapabilityNames.HostClockRead)));
    }
}

internal sealed record GetFlavorIndexRequest(string? Filter);

internal sealed record GetFlavorIndexResponse(
    ImmutableArray<string> Flavors,
    DateTimeOffset ObservedAt);

internal sealed class FlavorIndexOwner(IHostClockCapability clock)
    : IOperationOwner<GetFlavorIndexRequest, GetFlavorIndexResponse>
{
    private static readonly ImmutableArray<string> Flavors = ["salty", "sweet", "umami"];

    public OperationId OperationId { get; } = new(FlavorsModule.GetIndexOperationId);

    public async ValueTask<OperationResponse<GetFlavorIndexResponse>> HandleAsync(
        GetFlavorIndexRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = await clock.GetUtcNowAsync(cancellationToken);
        ImmutableArray<string> flavors = request.Filter is null
            ? Flavors
            : [.. Flavors.Where(flavor =>
                flavor.StartsWith(request.Filter, StringComparison.Ordinal))];
        var response = new GetFlavorIndexResponse(flavors, observedAt);
        return OperationResponse<GetFlavorIndexResponse>.Success(
            response,
            OperationResult.Ok(new Dictionary<string, object?>
            {
                ["flavors"] = response.Flavors,
                ["observedAt"] = response.ObservedAt,
                // The kernel projects the absolute URL; the module never sees an origin.
                ["@id"] = RouteReference.Endpoint(FlavorsModule.RouteName)
            }));
    }
}
