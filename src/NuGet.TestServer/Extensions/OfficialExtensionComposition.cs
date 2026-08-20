using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NuGet.TestServer.Extensions.Vulnerabilities;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Extensions;

internal sealed class OfficialExtensionComposition : IExtensionHealthSource
{
    private readonly ResolvedExtensionGraph _graph;
    private readonly bool _enableVulnerabilityPersistence;

    private OfficialExtensionComposition(
        ResolvedExtensionGraph graph,
        VulnerabilityExtension vulnerabilities,
        bool enableVulnerabilityPersistence)
    {
        _graph = graph;
        Vulnerabilities = vulnerabilities;
        _enableVulnerabilityPersistence = enableVulnerabilityPersistence;
    }

    public VulnerabilityExtension Vulnerabilities { get; }

    public VulnerabilitySnapshotProvider VulnerabilitySnapshots => Vulnerabilities.Snapshots;

    public static OfficialExtensionComposition Create(ServerComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return new OfficialExtensionComposition(
            composition.ExtensionGraph,
            new VulnerabilityExtension(
                composition.Vulnerabilities.Active,
                state: null,
                outbound: null,
                TimeProvider.System,
                _ => { }),
            composition.EnableVulnerabilityPersistence);
    }

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(this);
        services.AddSingleton(Vulnerabilities);
        services.AddSingleton<IHostedService>(provider =>
        {
            if (_enableVulnerabilityPersistence &&
                _graph.Capabilities.Any(capability =>
                    capability.ExtensionId == BuiltInExtensionIds.Vulnerabilities &&
                    capability.IsGranted &&
                    capability.Name == BuiltInCapabilityNames.ExtensionStateRead))
            {
                var capabilities = provider.GetRequiredService<CapabilityBroker>()
                    .ForOwner(BuiltInExtensionIds.Vulnerabilities);
                var logger = provider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("NuGet.TestServer.Extensions.Vulnerabilities");
                Vulnerabilities.Configure(
                    capabilities.GetRequired<IExtensionStateCapability>(
                        BuiltInCapabilityNames.ExtensionStateRead),
                    capabilities.GetRequired<IOutboundHttpCapability>(
                        BuiltInCapabilityNames.OutboundHttp),
                    warning => logger.LogWarning("{Warning}", warning));
            }

            return Vulnerabilities;
        });
    }

    public void RegisterOperations(OperationRegistryBuilder builder)
    {
        if (_graph.Extensions.Any(extension =>
                extension.Id == BuiltInExtensionIds.Vulnerabilities))
        {
            new VulnerabilityOperations(Vulnerabilities.Snapshots).Register(builder);
        }
    }

    public ExtensionHealthSnapshot GetHealth() =>
        new(
            Vulnerabilities.Health.Ready,
            Vulnerabilities.Health.Status,
            Vulnerabilities.Health.Warning);
}
