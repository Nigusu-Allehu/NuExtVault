using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.Control;
using NuExtVault.Extensions.ServiceIndex;
using NuExtVault.Extensions.Vulnerabilities;
using NuExtVault.Kernel;
using NuExtVault.Kernel.Capabilities;

namespace NuExtVault.Hosting;

/// <summary>
/// The composition root for the official extension bundle. This is the only place that
/// knows both the kernel and the official extension assembly: it instantiates the
/// official owners, hands them capabilities the kernel resolved by declared capability
/// identity, and adapts their lifecycle to the host. Everything it creates is scoped to
/// one host instance.
/// </summary>
internal sealed class OfficialExtensionComposition : IExtensionHealthSource
{
    private readonly ResolvedExtensionGraph _graph;
    private readonly bool _enableVulnerabilityPersistence;

    private OfficialExtensionComposition(
        ResolvedExtensionGraph graph,
        ControlExtension control,
        VulnerabilityExtension vulnerabilities,
        bool enableVulnerabilityPersistence)
    {
        _graph = graph;
        Control = control;
        Vulnerabilities = vulnerabilities;
        VulnerabilityCatalog = new VulnerabilityCatalogSource(vulnerabilities.Snapshots);
        _enableVulnerabilityPersistence = enableVulnerabilityPersistence;
    }

    public ControlExtension Control { get; }

    public VulnerabilityExtension Vulnerabilities { get; }

    public VulnerabilitySnapshotProvider VulnerabilitySnapshots => Vulnerabilities.Snapshots;

    /// <summary>
    /// The host-scoped catalog projection the kernel reads when it serves the
    /// vulnerability capability. The kernel never sees the feature's snapshot types.
    /// </summary>
    public IVulnerabilityCatalogSource VulnerabilityCatalog { get; }

    public static OfficialExtensionComposition Create(ServerComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return new OfficialExtensionComposition(
            composition.ExtensionGraph,
            new ControlExtension(),
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
                    .CreateLogger("NuExtVault.Extensions.Vulnerabilities");
                Vulnerabilities.Configure(
                    capabilities.GetRequired<IExtensionStateCapability>(
                        BuiltInCapabilityNames.ExtensionStateRead),
                    capabilities.GetRequired<IKernelOutboundHttpCapability>(
                        BuiltInCapabilityNames.OutboundHttp),
                    warning => logger.LogWarning("{Warning}", warning));
            }

            return new VulnerabilityExtensionHostedService(Vulnerabilities);
        });
    }

    public void RegisterOperations(
        OperationRegistryBuilder builder,
        CapabilityBroker broker,
        ServiceIndexResourceRegistry resources)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(resources);
        if (_graph.Extensions.Any(extension =>
                extension.Id == BuiltInExtensionIds.ServiceIndex))
        {
            new ServiceIndexOperations(resources.Resources).Register(builder);
        }

        if (_graph.Extensions.Any(extension =>
                extension.Id == BuiltInExtensionIds.TestControl))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.TestControl);
            Control.RegisterOperations(
                builder,
                capabilities.GetRequired<IPackageControlCapability>(
                    BuiltInCapabilityNames.ControlPackagesManage),
                capabilities.GetRequired<IKernelInstrumentationControlCapability>(
                    BuiltInCapabilityNames.ControlInstrumentationManage));
        }

        if (_graph.Extensions.Any(extension =>
                extension.Id == BuiltInExtensionIds.Vulnerabilities))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.Vulnerabilities);
            new VulnerabilityOperations(
                capabilities.GetRequired<IVulnerabilityCatalogCapability>(
                    BuiltInCapabilityNames.VulnerabilityStateRead)).Register(builder);
        }
    }

    public ExtensionHealthSnapshot GetHealth() =>
        new(
            Vulnerabilities.Health.Ready,
            Vulnerabilities.Health.Status,
            Vulnerabilities.Health.Warning);
}

/// <summary>
/// Adapts the official vulnerability feature's start and stop to the host lifecycle. The
/// feature itself never references a hosting type, so it stays compilable against the
/// extension abstractions alone.
/// </summary>
internal sealed class VulnerabilityExtensionHostedService(VulnerabilityExtension extension)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        extension.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        extension.StopAsync(cancellationToken);
}
