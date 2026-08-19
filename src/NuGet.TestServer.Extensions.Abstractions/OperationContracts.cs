namespace NuGet.TestServer.Extensions.Abstractions;

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
            "NuTest.Moderation.Moderate", OperationFamily.Moderation),
        Binding<GetModerationAuditRequest, GetModerationAuditResponse>(
            "NuTest.Moderation.GetAudit", OperationFamily.Moderation),
        Binding<GetPackageValidationsRequest, GetPackageValidationsResponse>(
            "NuTest.Moderation.GetValidations", OperationFamily.Moderation),
        Binding<GetVulnerabilityIndexRequest, GetVulnerabilityIndexResponse>(
            "NuGet.Vulnerabilities.GetIndex", OperationFamily.Vulnerabilities),
        Binding<GetVulnerabilityPageRequest, GetVulnerabilityPageResponse>(
            "NuGet.Vulnerabilities.GetPage", OperationFamily.Vulnerabilities),
        Binding<GetControlStateRequest, GetControlStateResponse>(
            "NuTest.Control.GetState", OperationFamily.TestControl),
        Binding<ResetControlStateRequest, ResetControlStateResponse>(
            "NuTest.Control.Reset", OperationFamily.TestControl),
        Binding<GetControlPackagesRequest, GetControlPackagesResponse>(
            "NuTest.Control.GetPackages", OperationFamily.TestControl),
        Binding<AddControlPackageRequest, AddControlPackageResponse>(
            "NuTest.Control.AddPackage", OperationFamily.TestControl),
        Binding<DeleteControlPackageRequest, DeleteControlPackageResponse>(
            "NuTest.Control.DeletePackage", OperationFamily.TestControl),
        Binding<RelistControlPackageRequest, RelistControlPackageResponse>(
            "NuTest.Control.RelistPackage", OperationFamily.TestControl),
        Binding<UnlistControlPackageRequest, UnlistControlPackageResponse>(
            "NuTest.Control.UnlistPackage", OperationFamily.TestControl),
        Binding<UpdatePackageMetadataRequest, UpdatePackageMetadataResponse>(
            "NuTest.Control.UpdatePackageMetadata", OperationFamily.TestControl),
        Binding<GetRequestsRequest, GetRequestsResponse>(
            "NuTest.Control.GetRequests", OperationFamily.TestControl),
        Binding<ClearRequestsRequest, ClearRequestsResponse>(
            "NuTest.Control.ClearRequests", OperationFamily.TestControl),
        Binding<GetFaultsRequest, GetFaultsResponse>(
            "NuTest.Control.GetFaults", OperationFamily.TestControl),
        Binding<AddFaultRequest, AddFaultResponse>(
            "NuTest.Control.AddFault", OperationFamily.TestControl),
        Binding<ClearFaultsRequest, ClearFaultsResponse>(
            "NuTest.Control.ClearFaults", OperationFamily.TestControl),
        Binding<GetLivenessRequest, GetLivenessResponse>(
            "NuTest.Health.GetLiveness", OperationFamily.Health),
        Binding<GetReadinessRequest, GetReadinessResponse>(
            "NuTest.Health.GetReadiness", OperationFamily.Health),
        Binding<GetStorageHealthRequest, GetStorageHealthResponse>(
            "NuTest.Health.GetStorage", OperationFamily.Health),
        Binding<GetDiagnosticsRequest, GetDiagnosticsResponse>(
            "NuTest.Diagnostics.Get", OperationFamily.Diagnostics),
        Binding<CreateBackupRequest, CreateBackupResponse>(
            "NuTest.Backup.Create", OperationFamily.Backup),
        Binding<RestoreBackupRequest, RestoreBackupResponse>(
            "NuTest.Restore.Execute", OperationFamily.Restore)
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
