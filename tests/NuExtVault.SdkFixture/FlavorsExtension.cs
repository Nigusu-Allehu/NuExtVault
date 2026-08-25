using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;
using NuExtVault.SdkFixture.Dependency;

namespace NuExtVault.SdkFixture;

public sealed class FlavorsExtension : IExtensionModule
{
    public ExtensionModuleContribution Contribution { get; } =
        ExtensionModuleContribution.FromManifest(
            ExtensionManifestJson.Parse(
                File.ReadAllBytes(Path.Combine(
                    AppContext.BaseDirectory,
                    "extension-manifest.json"))));

    public void RegisterRoutes(IRouteBinderRegistry routes)
    {
        routes.Bind<GetFlavorIndexRequest>(
            new RouteIdentity("contoso.flavors.index"),
            static (_, _) => ValueTask.FromResult(new GetFlavorIndexRequest()));
    }

    public void RegisterOperations(
        IOperationOwnerRegistry operations,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource contributions)
    {
        var clock = capabilities.GetRequired<IHostClockCapability>(
            new CapabilityRequest(
                new CapabilityIdentity("host.clock.read"),
                CapabilityRequirement.Required));
        operations.RegisterNew<GetFlavorIndexRequest, GetFlavorIndexResponse>(
            Contribution.Manifest.Identity.Id,
            new OperationIdentity("Contoso.Flavors.GetIndex"),
            async (_, token) =>
            {
                var now = await clock.GetUtcNowAsync(token);
                return OperationResponse<GetFlavorIndexResponse>.Success(
                    new GetFlavorIndexResponse([FlavorDependency.Name], now));
            });
    }
}

public sealed record GetFlavorIndexRequest;

public sealed record GetFlavorIndexResponse(
    ImmutableArray<string> Flavors,
    DateTimeOffset GeneratedAt);
