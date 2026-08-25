using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Hosting;

namespace NuExtVault.Kernel;

/// <summary>
/// Validated, per-host projection of profile-selected service-index contributions.
/// The kernel is the only component that turns relative route names into public URLs.
/// </summary>
internal sealed class ServiceIndexResourceRegistry
{
    private readonly ImmutableArray<ServiceResourceDescriptor> _resources;

    private ServiceIndexResourceRegistry(
        ImmutableArray<ServiceResourceDescriptor> resources) =>
        _resources = resources;

    public static ServiceIndexResourceRegistry Create(ResolvedExtensionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var resources = graph.Resources
            .Select(resource =>
            {
                var contribution = resource.Contribution;
                var endpoint = graph.Endpoints.Single(candidate =>
                    StringComparer.OrdinalIgnoreCase.Equals(
                        candidate.ExtensionId,
                        resource.ExtensionId) &&
                    candidate.Descriptor.Operations.Any(operation =>
                        operation.OperationId == contribution.OperationId.Value) &&
                    Matches(contribution.RouteName, candidate.Descriptor.PathTemplate));
                var route = contribution.RouteName.EndsWith('/')
                    ? RouteReference.Base(endpoint.Descriptor.Name)
                    : RouteReference.Endpoint(endpoint.Descriptor.Name);
                return (contribution, descriptor: new ServiceResourceDescriptor(
                    route,
                    contribution.AdvertisedType,
                    contribution.Comment));
            })
            .OrderBy(resource => resource.contribution.Order)
            .ThenBy(resource => resource.contribution.AdvertisedType, StringComparer.Ordinal)
            .Select(resource => resource.descriptor)
            .ToImmutableArray();
        return new ServiceIndexResourceRegistry(resources);
    }

    public ImmutableArray<ServiceResourceDescriptor> Resources => _resources;

    private static bool Matches(string routeName, string template) =>
        StringComparer.Ordinal.Equals(routeName, template) ||
        (routeName.EndsWith('/') && template.StartsWith(routeName, StringComparison.Ordinal));
}
