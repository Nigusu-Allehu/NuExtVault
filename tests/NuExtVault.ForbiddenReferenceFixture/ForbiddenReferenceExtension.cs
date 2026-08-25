using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.Vulnerabilities;
using NuExtVault.Hosting;
using NuExtVault.Packages;

namespace NuExtVault.ForbiddenReferenceFixture;

/// <summary>
/// Step 20 tests-first red phase fixture. A separately compiled extension
/// module that is otherwise well formed but genuinely references the host
/// assembly (<see cref="NuExtVaultHost"/>), the kernel assembly
/// (<see cref="TestPackageBuilder"/>), and the official extensions assembly
/// (<see cref="EmbeddedVulnerabilitySnapshot"/>). No production code depends on
/// this assembly; it exists only so unit tests can prove the
/// external extension loader rejects packages whose entry assembly references
/// any of those three forbidden assemblies.
/// </summary>
public sealed class ForbiddenReferenceExtension : IExtensionModule
{
    public const string ExtensionId = "contoso.forbidden-reference";

    public ExtensionModuleContribution Contribution { get; } =
        ExtensionModuleContribution.FromManifest(new ExtensionManifest(
            new ManifestSchemaVersion(1),
            new ExtensionIdentity(ExtensionId, "1.0.0", "Contoso"),
            new SdkCompatibilityRange(ExtensionSdkVersions.OldestSupported, ExtensionSdkVersions.Current),
            new ContractVersionSet(
                ExtensionSdkVersions.ManifestV1,
                ExtensionSdkVersions.OperationV1,
                ExtensionSdkVersions.ContributionV1,
                ExtensionSdkVersions.RouteV1,
                ExtensionSdkVersions.CapabilityV1,
                ExtensionSdkVersions.StructuralV1),
            [],
            [],
            [],
            []));

    public void RegisterOperations(
        IOperationOwnerRegistry operations,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource contributions)
    {
    }

    /// <summary>
    /// Never called by any test. Its only purpose is to force genuine
    /// AssemblyRef metadata to the host, kernel, and official assemblies so a
    /// static or load-time reference scan has something real to detect.
    /// </summary>
    internal static object[] TouchForbiddenAssemblies() =>
        [
            typeof(NuExtVaultHost),
            typeof(TestPackageBuilder),
            typeof(EmbeddedVulnerabilitySnapshot)
        ];
}
