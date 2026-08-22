using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Extensions.Control;
using NuGet.TestServer.Extensions.Official;
using NuGet.TestServer.Extensions.ServiceIndex;
using NuGet.TestServer.Extensions.SupplyChain;
using NuGet.TestServer.Extensions.Vulnerabilities;
using NuGet.TestServer.Hosting.Endpoints;

namespace NuGet.TestServer.Hosting;

/// <summary>
/// The built-in extension manifests. Routes are declared as typed endpoint descriptors
/// and are the only source of the generated route table.
/// </summary>
internal static class BuiltInExtensionCatalog
{
    private static readonly ExtensionVersion Version = new(1, 0, 0);
    private static readonly ExtensionVersionRange Compatibility = ExtensionVersionRange.Major(1);

    public static ImmutableArray<ExtensionManifest> Manifests { get; } =
    [
        Manifest(
            BuiltInExtensionIds.Protocol),
        Manifest(
            BuiltInExtensionIds.ServiceIndex,
            operations: Operations(OperationFamily.ServiceIndex),
            endpoints: ProtocolEndpoints.ServiceIndex),
        Manifest(
            BuiltInExtensionIds.Vulnerabilities,
            operations: Operations(OperationFamily.Vulnerabilities),
            endpoints: VulnerabilityEndpoints.Descriptors,
            resources:
            [
                Resource(
                    "VulnerabilityInfo",
                    "6.7.0",
                    OperationIds.VulnerabilitiesGetIndex,
                    "/v3/vulnerabilities/index.json",
                    order: 70)
            ],
            capabilities:
            [
                Required(BuiltInCapabilityNames.VulnerabilityStateRead)
            ]),
        Manifest(
            BuiltInExtensionIds.TestControl,
            operations: Operations(OperationFamily.TestControl),
            endpoints: ControlEndpoints.Descriptors,
            capabilities:
            [
                Required(BuiltInCapabilityNames.ControlPackagesManage),
                Required(BuiltInCapabilityNames.ControlInstrumentationManage)
            ]),
        Manifest(
            BuiltInExtensionIds.DurableStorage,
            capabilities: [Required(BuiltInCapabilityNames.DurableStorage)]),
        Manifest(
            BuiltInExtensionIds.SupplyChain,
            operations: Operations(OperationFamily.Moderation),
            endpoints: ModerationEndpoints.Descriptors,
            capabilities:
            [
                Required(BuiltInCapabilityNames.ModerationRead),
                Required(BuiltInCapabilityNames.ModerationDecide)
            ]),
        // Official modules contribute their own manifests through the module seam.
        .. OfficialExtensionModules.Manifests
    ];

    public static ExtensionCatalog Instance { get; } =
        CreateWith([new SupplyChainExtension()]);

    /// <summary>
    /// Creates a catalog that also contains separately compiled modules. Adding a route
    /// requires a descriptor, a binder, and an owner in the module; it never requires a
    /// change to kernel routing, composition, or catalog source.
    /// </summary>
    public static ExtensionCatalog CreateWith(IEnumerable<IExtensionModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return new ExtensionCatalog(
            Manifests.Concat(modules.Select(module => module.Contribution.Manifest)));
    }

    private static ExtensionManifest Manifest(
        string id,
        ImmutableArray<ExtensionDependency> dependencies = default,
        ImmutableArray<string> operations = default,
        ImmutableArray<EndpointDescriptor> endpoints = default,
        ImmutableArray<ServiceResourceContribution> resources = default,
        ImmutableArray<CapabilityRequest> capabilities = default) =>
        new(
            1,
            id,
            Version,
            Compatibility,
            dependencies.IsDefault ? [] : dependencies,
            operations.IsDefault ? [] : operations,
            endpoints.IsDefault ? [] : endpoints,
            resources.IsDefault ? [] : resources,
            capabilities.IsDefault ? [] : capabilities);

    private static ExtensionDependency Dependency(string id) => new(id, Compatibility);

    private static CapabilityRequest Required(string capability) => new(capability, true);

    private static ServiceResourceContribution Resource(
        string resourceType,
        string version,
        string operationId,
        string routeName,
        ServiceResourceAccess access = ServiceResourceAccess.Read,
        ImmutableArray<string> producesUrlsFor = default,
        ImmutableArray<string> requiresResourceTypes = default,
        string? comment = null,
        int order = 0) =>
        new(
            resourceType,
            version,
            new OperationId(operationId),
            routeName,
            ServiceResourceVisibility.Advertised,
            access,
            producesUrlsFor.IsDefault ? [] : producesUrlsFor,
            requiresResourceTypes.IsDefault ? [] : requiresResourceTypes,
            comment,
            order,
            ServiceResourceReadiness.Ready);

    private static ImmutableArray<string> Operations(params OperationFamily[] families) =>
    [
        .. OperationContracts.All
            .Where(contract => families.Contains(contract.Family))
            .Select(contract => contract.Id.Value)
            .Order(StringComparer.Ordinal)
    ];
}
