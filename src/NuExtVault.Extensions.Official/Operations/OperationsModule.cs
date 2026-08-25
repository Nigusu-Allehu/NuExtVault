using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Extensions.Operations;

/// <summary>
/// The official operational extension. It owns the health, diagnostics, backup, and
/// restore operations and contributes its routes through the generic module seam.
/// </summary>
internal sealed class OperationsModule : IExtensionModule
{
    public const string ExtensionId = "builtin.operations";

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [
                .. OperationContracts.All
                    .Where(contract =>
                        contract.Family == OperationFamily.Health ||
                        contract.Family == OperationFamily.Diagnostics ||
                        contract.Family == OperationFamily.Backup ||
                        contract.Family == OperationFamily.Restore)
                    .Select(contract => contract.Id.Value)
                    .Order(StringComparer.Ordinal)
            ],
            OperationsEndpoints.Descriptors,
            [],
            [
                new CapabilityRequest(BuiltInCapabilityNames.OperationsQuery, true),
                new CapabilityRequest(BuiltInCapabilityNames.BackupInvoke, true),
                new CapabilityRequest(BuiltInCapabilityNames.RestoreInvoke, true)
            ]),
        []);

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource documentContributions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        new OperationsOperations(
            capabilities.GetRequired<IOperationsQueryCapability>(
                BuiltInCapabilityNames.OperationsQuery),
            capabilities.GetRequired<IBackupCheckpointCapability>(
                BuiltInCapabilityNames.BackupInvoke),
            capabilities.GetRequired<IRestoreCheckpointCapability>(
                BuiltInCapabilityNames.RestoreInvoke)).Register(registry);
    }
}

internal static class OperationsEndpoints
{
    private static IEndpointHandler Liveness { get; } =
        EndpointHandler.Create<GetLivenessRequest, GetLivenessResponse>(
            OperationIds.HealthGetLiveness,
            _ => new GetLivenessRequest());

    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
    [
        new()
        {
            Name = "health.live",
            Methods = ["GET"],
            PathTemplate = "/health/live",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Anonymous),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetLivenessRequest, GetLivenessResponse>(
                    OperationIds.HealthGetLiveness)
            ],
            Handler = Liveness
        },
        new()
        {
            Name = "health.live-legacy",
            Methods = ["GET"],
            PathTemplate = "/__test/health",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Anonymous),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetLivenessRequest, GetLivenessResponse>(
                    OperationIds.HealthGetLiveness)
            ],
            Handler = Liveness
        },
        new()
        {
            Name = "health.ready",
            Methods = ["GET"],
            PathTemplate = "/health/ready",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Anonymous),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetReadinessRequest, GetReadinessResponse>(
                    OperationIds.HealthGetReadiness)
            ],
            Handler = EndpointHandler.Create<GetReadinessRequest, GetReadinessResponse>(
                OperationIds.HealthGetReadiness,
                _ => new GetReadinessRequest())
        },
        new()
        {
            Name = "health.storage",
            Methods = ["GET"],
            PathTemplate = "/health/storage",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetStorageHealthRequest, GetStorageHealthResponse>(
                    OperationIds.HealthGetStorage)
            ],
            Handler = EndpointHandler.Create<GetStorageHealthRequest, GetStorageHealthResponse>(
                OperationIds.HealthGetStorage,
                _ => new GetStorageHealthRequest())
        }
    ];
}
