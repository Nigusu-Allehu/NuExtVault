using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Extensions.Official;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Kernel.Owners;

namespace NuGet.TestServer.Hosting;

/// <summary>
/// Composes the owner adapters for one host instance. Only extensions the resolved graph
/// activates contribute owners, so ownership never depends on registration order or on
/// which services happen to exist. The official bundle is selected explicitly here, in
/// the composition root; the kernel never names it.
/// </summary>
internal static class BuiltInOperationOwners
{
    public static OperationRegistry CreateRegistry(
        CapabilityBroker broker,
        ResolvedExtensionGraph graph,
        ServiceIndexResourceRegistry resources,
        OfficialExtensionComposition officialExtensions,
        ImmutableArray<IExtensionModule> modules = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(officialExtensions);
        modules = modules.IsDefault ? [] : modules;
        var builder = new OperationRegistryBuilder();
        var selected = graph.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        officialExtensions.RegisterOperations(builder, broker, resources);

        if (selected.Contains(BuiltInExtensionIds.SupplyChain))
        {
            var capabilities = broker.ForOwner(BuiltInExtensionIds.SupplyChain);
            new ModerationOperations(
                capabilities.GetRequired<IModerationCapability>(
                    BuiltInCapabilityNames.ModerationRead)).Register(builder);
        }

        // Official and separately compiled modules are composed through the same generic
        // seam: the kernel never names a module, its operations, its routes, or its
        // capabilities.
        var allModules = OfficialExtensionModules.All.Concat(modules).ToArray();
        var documentContributors = DocumentContributorRegistry.Create(graph, allModules, broker);
        foreach (var module in allModules
                     .Where(module => selected.Contains(module.Contribution.Manifest.Id))
                     .OrderBy(
                         module => module.Contribution.Manifest.Id,
                         StringComparer.Ordinal))
        {
            var moduleId = module.Contribution.Manifest.Id;
            module.RegisterOperations(
                builder,
                broker.ForOwner(moduleId),
                documentContributors);
        }

        return builder.Build(graph, ExtensionModules.CreateContractIndex(modules));
    }
}
