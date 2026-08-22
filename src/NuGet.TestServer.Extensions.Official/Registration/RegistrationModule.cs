using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Registration;

internal sealed class RegistrationModule : IExtensionModule
{
    public const string ExtensionId = "builtin.registration";

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [
                new ExtensionDependency(
                    "builtin.flat-container",
                    ExtensionVersionRange.Major(1))
            ],
            [
                .. OperationContracts.All
                    .Where(contract => contract.Family == OperationFamily.Registration)
                    .Select(contract => contract.Id.Value)
                    .Order(StringComparer.Ordinal)
            ],
            RegistrationEndpoints.All,
            [
                new ServiceResourceContribution(
                    "RegistrationsBaseUrl",
                    "3.6.0",
                    new OperationId(OperationIds.RegistrationGetIndex),
                    "/registration/",
                    ServiceResourceVisibility.Advertised,
                    ServiceResourceAccess.Read,
                    ["PackageBaseAddress/3.0.0"],
                    ["PackageBaseAddress/3.0.0"],
                    null,
                    20,
                    ServiceResourceReadiness.Ready)
            ],
            [
                new CapabilityRequest(BuiltInCapabilityNames.PackagesMetadataRead, true),
                new CapabilityRequest(BuiltInCapabilityNames.VulnerabilityStateRead, true)
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
        new RegistrationOperations(
            capabilities.GetRequired<IRegistrationMetadataReadCapability>(
                BuiltInCapabilityNames.PackagesMetadataRead),
            capabilities.GetRequired<IRegistrationVulnerabilityReadCapability>(
                BuiltInCapabilityNames.VulnerabilityStateRead),
            documentContributions).Register(registry);
    }
}
