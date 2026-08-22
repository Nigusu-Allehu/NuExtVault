using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Extensions.PackageManagement;

internal sealed class PackageManagementModule : IExtensionModule
{
    public const string ExtensionId = "builtin.package-management";

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [
                new ExtensionDependency(
                    BuiltInExtensionIds.Protocol,
                    ExtensionVersionRange.Major(1)),
                new ExtensionDependency(
                    BuiltInExtensionIds.SupplyChainPolicy,
                    ExtensionVersionRange.Major(1))
            ],
            [
                .. OperationContracts.All
                    .Where(contract => contract.Family == OperationFamily.PackageManagement)
                    .Select(contract => contract.Id.Value)
                    .Order(StringComparer.Ordinal)
            ],
            PackageManagementEndpoints.All,
            [
                new ServiceResourceContribution(
                    "PackagePublish",
                    "2.0.0",
                    new OperationId(OperationIds.PackageManagementPush),
                    "/package",
                    ServiceResourceVisibility.Advertised,
                    ServiceResourceAccess.PackagePublish,
                    [],
                    [],
                    null,
                    50,
                    ServiceResourceReadiness.Ready),
                new ServiceResourceContribution(
                    "SymbolPackagePublish",
                    "4.9.0",
                    new OperationId(OperationIds.PackageManagementPushSymbols),
                    "/symbolpackage",
                    ServiceResourceVisibility.Advertised,
                    ServiceResourceAccess.Write,
                    [],
                    [],
                    null,
                    60,
                    ServiceResourceReadiness.Ready)
            ],
            [
                new CapabilityRequest(BuiltInCapabilityNames.PackagesMetadataRead, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesContentWrite, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesPublish, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesUnlist, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesRelist, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesDelete, true)
            ]),
        []);

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource documentContributions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(documentContributions);
        new PackageManagementOperations(
            capabilities.GetRequired<IPackagePushCapability>(
                BuiltInCapabilityNames.PackagesPublish),
            capabilities.GetRequired<IPackageSymbolsPushCapability>(
                BuiltInCapabilityNames.PackagesContentWrite),
            capabilities.GetRequired<IPackageManagementListCapability>(
                BuiltInCapabilityNames.PackagesMetadataRead),
            capabilities.GetRequired<IPackageUnlistCapability>(
                BuiltInCapabilityNames.PackagesUnlist),
            capabilities.GetRequired<IPackageRelistCapability>(
                BuiltInCapabilityNames.PackagesRelist),
            capabilities.GetRequired<IPackageDeleteCapability>(
                BuiltInCapabilityNames.PackagesDelete)).Register(registry);
    }
}
