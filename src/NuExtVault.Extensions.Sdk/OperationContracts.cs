namespace NuExtVault.Extensions.Sdk;

internal static class OperationContracts
{
    internal static IReadOnlyList<OperationBinding> Bindings { get; } =
    [
        Binding<GetServiceIndexRequest, GetServiceIndexResponse>(
            "NuGet.ServiceIndex.Get", OperationFamily.ServiceIndex),
        Binding<GetPackageVersionsRequest, GetPackageVersionsResponse>(
            "NuGet.FlatContainer.GetVersions", OperationFamily.FlatContainer),
        Binding<GetPackageRequest, GetPackageResponse>(
            "NuGet.FlatContainer.GetPackage", OperationFamily.FlatContainer),
        Binding<GetNuspecRequest, GetNuspecResponse>(
            "NuGet.FlatContainer.GetNuspec", OperationFamily.FlatContainer),
        Binding<GetPackageHashRequest, GetPackageHashResponse>(
            "NuGet.FlatContainer.GetHash", OperationFamily.FlatContainer),
        Binding<GetSymbolRequest, GetSymbolResponse>(
            "NuGet.FlatContainer.GetSymbol", OperationFamily.FlatContainer),
        Binding<GetRegistrationIndexRequest, GetRegistrationIndexResponse>(
            "NuGet.Registration.GetIndex", OperationFamily.Registration),
        Binding<GetRegistrationPageRequest, GetRegistrationPageResponse>(
            "NuGet.Registration.GetPage", OperationFamily.Registration),
        Binding<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
            "NuGet.Registration.GetLeaf", OperationFamily.Registration),
        Binding<SearchRequest, SearchResponse>(
            "NuGet.Search.Query", OperationFamily.Search),
        Binding<PushPackageRequest, PushPackageResponse>(
            "NuGet.PackageManagement.Push", OperationFamily.PackageManagement),
        Binding<PushSymbolsRequest, PushSymbolsResponse>(
            "NuGet.PackageManagement.PushSymbols", OperationFamily.PackageManagement),
        Binding<ListPackagesRequest, ListPackagesResponse>(
            "NuGet.PackageManagement.List", OperationFamily.PackageManagement),
        Binding<UnlistPackageRequest, UnlistPackageResponse>(
            "NuGet.PackageManagement.Unlist", OperationFamily.PackageManagement),
        Binding<RelistPackageRequest, RelistPackageResponse>(
            "NuGet.PackageManagement.Relist", OperationFamily.PackageManagement),
        Binding<DeletePackageRequest, DeletePackageResponse>(
            "NuGet.PackageManagement.Delete", OperationFamily.PackageManagement),
        Binding<ModeratePackageRequest, ModeratePackageResponse>(
            "NuExtVault.Moderation.Moderate", OperationFamily.Moderation),
        Binding<GetModerationAuditRequest, GetModerationAuditResponse>(
            "NuExtVault.Moderation.GetAudit", OperationFamily.Moderation),
        Binding<GetPackageValidationsRequest, GetPackageValidationsResponse>(
            "NuExtVault.Moderation.GetValidations", OperationFamily.Moderation),
        Binding<GetVulnerabilityIndexRequest, GetVulnerabilityIndexResponse>(
            "NuGet.Vulnerabilities.GetIndex", OperationFamily.Vulnerabilities),
        Binding<GetVulnerabilityPageRequest, GetVulnerabilityPageResponse>(
            "NuGet.Vulnerabilities.GetPage", OperationFamily.Vulnerabilities),
        Binding<GetControlStateRequest, GetControlStateResponse>(
            "NuExtVault.Control.GetState", OperationFamily.TestControl),
        Binding<ResetControlStateRequest, ResetControlStateResponse>(
            "NuExtVault.Control.Reset", OperationFamily.TestControl),
        Binding<GetControlPackagesRequest, GetControlPackagesResponse>(
            "NuExtVault.Control.GetPackages", OperationFamily.TestControl),
        Binding<AddControlPackageRequest, AddControlPackageResponse>(
            "NuExtVault.Control.AddPackage", OperationFamily.TestControl),
        Binding<DeleteControlPackageRequest, DeleteControlPackageResponse>(
            "NuExtVault.Control.DeletePackage", OperationFamily.TestControl),
        Binding<RelistControlPackageRequest, RelistControlPackageResponse>(
            "NuExtVault.Control.RelistPackage", OperationFamily.TestControl),
        Binding<UnlistControlPackageRequest, UnlistControlPackageResponse>(
            "NuExtVault.Control.UnlistPackage", OperationFamily.TestControl),
        Binding<UpdatePackageMetadataRequest, UpdatePackageMetadataResponse>(
            "NuExtVault.Control.UpdatePackageMetadata", OperationFamily.TestControl),
        Binding<GetRequestsRequest, GetRequestsResponse>(
            "NuExtVault.Control.GetRequests", OperationFamily.TestControl),
        Binding<ClearRequestsRequest, ClearRequestsResponse>(
            "NuExtVault.Control.ClearRequests", OperationFamily.TestControl),
        Binding<GetFaultsRequest, GetFaultsResponse>(
            "NuExtVault.Control.GetFaults", OperationFamily.TestControl),
        Binding<AddFaultRequest, AddFaultResponse>(
            "NuExtVault.Control.AddFault", OperationFamily.TestControl),
        Binding<ClearFaultsRequest, ClearFaultsResponse>(
            "NuExtVault.Control.ClearFaults", OperationFamily.TestControl),
        Binding<GetLivenessRequest, GetLivenessResponse>(
            "NuExtVault.Health.GetLiveness", OperationFamily.Health),
        Binding<GetReadinessRequest, GetReadinessResponse>(
            "NuExtVault.Health.GetReadiness", OperationFamily.Health),
        Binding<GetStorageHealthRequest, GetStorageHealthResponse>(
            "NuExtVault.Health.GetStorage", OperationFamily.Health),
        Binding<GetDiagnosticsRequest, GetDiagnosticsResponse>(
            "NuExtVault.Diagnostics.Get", OperationFamily.Diagnostics),
        Binding<CreateBackupRequest, CreateBackupResponse>(
            "NuExtVault.Backup.Create", OperationFamily.Backup),
        Binding<RestoreBackupRequest, RestoreBackupResponse>(
            "NuExtVault.Restore.Execute", OperationFamily.Restore)
    ];

    internal static IReadOnlyList<OperationContract> All { get; } =
        Bindings.Select(binding => binding.Contract).ToArray();

    private static OperationBinding Binding<TRequest, TResponse>(
        string id,
        OperationFamily family) =>
        new(
            new OperationContract(
                new OperationId(id),
                family,
                1,
                $"{typeof(TRequest).Name}.v1",
                $"{typeof(TResponse).Name}.v1"),
            typeof(TRequest),
            typeof(TResponse));
}
