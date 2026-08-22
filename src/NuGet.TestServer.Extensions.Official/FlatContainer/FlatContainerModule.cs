using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.FlatContainer;

/// <summary>
/// The official <c>NuGet.FlatContainer</c> extension. It is the sole owner of the
/// package version, content, nuspec, hash, and symbol read operations, of the
/// flat-container routes, and of the advertised <c>PackageBaseAddress</c> resource. It
/// contributes them through the same module seam a separately compiled module uses.
/// </summary>
internal sealed class FlatContainerModule : IExtensionModule
{
    public const string ExtensionId = "builtin.flat-container";

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [
                .. OperationContracts.All
                    .Where(contract => contract.Family == OperationFamily.FlatContainer)
                    .Select(contract => contract.Id.Value)
                    .Order(StringComparer.Ordinal)
            ],
            FlatContainerEndpoints.Descriptors,
            [
                new ServiceResourceContribution(
                    "PackageBaseAddress",
                    "3.0.0",
                    new OperationId(OperationIds.FlatContainerGetVersions),
                    "/flatcontainer/",
                    ServiceResourceVisibility.Advertised,
                    ServiceResourceAccess.Read,
                    [],
                    [],
                    null,
                    10,
                    ServiceResourceReadiness.Ready)
            ],
            [
                new CapabilityRequest(BuiltInCapabilityNames.PackagesIdentityRead, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesMetadataRead, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesContentRead, true),
                new CapabilityRequest(BuiltInCapabilityNames.PackagesSymbolsRead, true)
            ]),
        // The flat-container operation contracts are part of the shared protocol contract
        // index, so this module introduces no new contract.
        []);

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource documentContributions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        new FlatContainerOperations(
            capabilities.GetRequired<IPackageMetadataReadCapability>(
                BuiltInCapabilityNames.PackagesMetadataRead),
            capabilities.GetRequired<IPackageContentReadCapability>(
                BuiltInCapabilityNames.PackagesContentRead),
            capabilities.GetRequired<IPackageSymbolReadCapability>(
                BuiltInCapabilityNames.PackagesSymbolsRead)).Register(registry);
    }
}
