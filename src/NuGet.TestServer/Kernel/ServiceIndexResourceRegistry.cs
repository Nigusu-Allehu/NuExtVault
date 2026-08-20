using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// Validated, per-host projection of profile-selected service-index contributions.
/// The kernel is the only component that turns relative route names into public URLs.
/// </summary>
internal sealed class ServiceIndexResourceRegistry
{
    private readonly ImmutableArray<ServiceResourceContribution> _contributions;

    private ServiceIndexResourceRegistry(
        ImmutableArray<ServiceResourceContribution> contributions) =>
        _contributions = contributions;

    public static ServiceIndexResourceRegistry Create(ResolvedExtensionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new ServiceIndexResourceRegistry(
        [
            .. graph.Resources
                .Select(resource => resource.Contribution)
                .OrderBy(resource => resource.Order)
                .ThenBy(resource => resource.AdvertisedType, StringComparer.Ordinal)
        ]);
    }

    public ImmutableArray<ServiceResourceDescriptor> Project(string baseAddress)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var root) ||
            root.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "The service-index base address must be an absolute HTTP or HTTPS URL.");
        }

        var normalizedRoot = baseAddress.TrimEnd('/');
        return
        [
            .. _contributions.Select(resource => new ServiceResourceDescriptor(
                $"{normalizedRoot}{resource.RouteName}",
                resource.AdvertisedType,
                resource.Comment))
        ];
    }
}
