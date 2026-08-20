using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Kernel.Owners;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// Composes the built-in owner adapters for one host instance. Only extensions the
/// resolved graph activates contribute owners, so ownership never depends on
/// registration order or on which services happen to exist.
/// </summary>
internal static class BuiltInOperationOwners
{
    public static OperationRegistry CreateRegistry(
        CapabilityBroker broker,
        ResolvedExtensionGraph graph,
        PackageTransferLimits limits)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(limits);
        var builder = new OperationRegistryBuilder();
        var selected = graph.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selected.Contains(BuiltInExtensionIds.Protocol))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.Protocol);
            var packages = capabilities.GetRequired<IPackageReadCapability>(
                BuiltInCapabilityNames.PackagesMetadataRead);
            new ProtocolReadOperations(
                packages).Register(builder);
            new RegistrationSearchOperations(
                packages,
                capabilities.GetRequired<IVulnerabilityReadCapability>(
                    BuiltInCapabilityNames.VulnerabilityStateRead)).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.Publication))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.Publication);
            new PublicationOperations(
                capabilities.GetRequired<IPackageReadCapability>(
                    BuiltInCapabilityNames.PackagesMetadataRead),
                capabilities.GetRequired<IPackageMutationCapability>(
                    BuiltInCapabilityNames.PackagesContentWrite),
                capabilities.GetRequired<IPublicationCapability>(
                    BuiltInCapabilityNames.PackagesPublish),
                capabilities.GetRequired<ITypedEventPublisher>(
                    BuiltInCapabilityNames.EventsPublish),
                limits).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.Vulnerabilities))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.Vulnerabilities);
            new VulnerabilityOperations(
                capabilities.GetRequired<IVulnerabilityReadCapability>(
                    BuiltInCapabilityNames.VulnerabilityStateRead)).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.SupplyChain))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.SupplyChain);
            new ModerationOperations(
                capabilities.GetRequired<IModerationCapability>(
                    BuiltInCapabilityNames.ModerationRead)).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.TestControl))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.TestControl);
            new ControlOperations(
                capabilities.GetRequired<IPackageReadCapability>(
                    BuiltInCapabilityNames.PackagesMetadataRead),
                capabilities.GetRequired<IPackageMutationCapability>(
                    BuiltInCapabilityNames.PackagesMetadataWrite),
                capabilities.GetRequired<IPublicationCapability>(
                    BuiltInCapabilityNames.PackagesPublish),
                capabilities.GetRequired<IControlInstrumentationCapability>(
                    BuiltInCapabilityNames.ControlQuery),
                capabilities.GetRequired<ITypedEventPublisher>(
                    BuiltInCapabilityNames.EventsPublish),
                limits).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.Operations))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.Operations);
            new ServerOperationsOperations(
                capabilities.GetRequired<IServerOperationsCapability>(
                    BuiltInCapabilityNames.OperationsQuery)).Register(builder);
        }

        return builder.Build(graph);
    }
}
