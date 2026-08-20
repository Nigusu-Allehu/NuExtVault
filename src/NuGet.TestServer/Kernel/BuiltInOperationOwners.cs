using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Owners;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Requests;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// Composes the built-in owner adapters for one host instance. Only extensions the
/// resolved graph activates contribute owners, so ownership never depends on
/// registration order or on which services happen to exist.
/// </summary>
internal static class BuiltInOperationOwners
{
    public static OperationRegistry CreateRegistry(
        IServiceProvider services,
        ResolvedExtensionGraph graph,
        ServerComposition composition)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(composition);
        var builder = new OperationRegistryBuilder();
        var selected = graph.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selected.Contains(BuiltInExtensionIds.Protocol))
        {
            new ProtocolReadOperations(
                services.GetRequiredService<IPackageStore>(),
                services.GetRequiredService<IPackageCandidateStore>(),
                services.GetRequiredService<PackageVisibilityPolicy>()).Register(builder);
            new RegistrationSearchOperations(
                services.GetRequiredService<IPackageStore>(),
                services.GetRequiredService<IPackageCandidateStore>(),
                services.GetRequiredService<PackageVisibilityPolicy>(),
                services.GetRequiredService<VulnerabilitySnapshotProvider>()).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.Publication))
        {
            new PublicationOperations(
                services.GetRequiredService<IPackageStore>(),
                services.GetRequiredService<PackageSupplyChainService>(),
                services.GetRequiredService<ServerDiagnostics>(),
                services.GetRequiredService<PackageTransferLimits>()).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.Vulnerabilities))
        {
            new VulnerabilityOperations(
                services.GetRequiredService<VulnerabilitySnapshotProvider>()).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.SupplyChain))
        {
            new ModerationOperations(
                services.GetRequiredService<PackageSupplyChainService>()).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.TestControl))
        {
            new ControlOperations(
                services.GetRequiredService<IPackageStore>(),
                services.GetRequiredService<PackageSupplyChainService>(),
                services.GetRequiredService<FaultRuleStore>(),
                services.GetRequiredService<RequestRecorder>(),
                services.GetRequiredService<ServerDiagnostics>(),
                services.GetRequiredService<PackageTransferLimits>()).Register(builder);
        }

        if (selected.Contains(BuiltInExtensionIds.Operations))
        {
            new ServerOperationsOperations(
                services.GetRequiredService<StorageHealth>(),
                services.GetRequiredService<ServerDiagnostics>(),
                composition.Hosting,
                composition.StorageDirectory).Register(builder);
        }

        return builder.Build(graph);
    }
}
