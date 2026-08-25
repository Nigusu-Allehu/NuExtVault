using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Kernel;

/// <summary>
/// Operations that are active but intentionally have no HTTP route in the current
/// protocol surface. They remain dispatchable through the registry.
/// </summary>
internal static class KernelOperationRoutes
{
    public static IReadOnlyList<string> NonRoutedOperations { get; } =
    [
        OperationIds.BackupCreate,
        OperationIds.DiagnosticsGet,
        OperationIds.FlatContainerGetSymbol,
        OperationIds.PackageManagementList,
        OperationIds.PackageManagementRelist,
        OperationIds.RestoreExecute
    ];

    /// <summary>
    /// Operations whose route exists only when the host runs with production identity.
    /// </summary>
    public static IReadOnlyList<string> ProductionOnlyRoutedOperations { get; } =
    [
        OperationIds.PackageManagementDelete
    ];
}
