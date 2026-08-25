namespace NuExtVault.Extensions.Sdk;

/// <summary>
/// Stable operation identifiers. They are contract identity shared by the kernel, the
/// official extension assembly, and any separately compiled module, so they live with
/// the contracts rather than with either implementation.
/// </summary>
internal static class OperationIds
{
    public const string ServiceIndexGet = "NuGet.ServiceIndex.Get";
    public const string FlatContainerGetVersions = "NuGet.FlatContainer.GetVersions";
    public const string FlatContainerGetPackage = "NuGet.FlatContainer.GetPackage";
    public const string FlatContainerGetNuspec = "NuGet.FlatContainer.GetNuspec";
    public const string FlatContainerGetHash = "NuGet.FlatContainer.GetHash";
    public const string FlatContainerGetSymbol = "NuGet.FlatContainer.GetSymbol";
    public const string RegistrationGetIndex = "NuGet.Registration.GetIndex";
    public const string RegistrationGetPage = "NuGet.Registration.GetPage";
    public const string RegistrationGetLeaf = "NuGet.Registration.GetLeaf";
    public const string SearchQuery = "NuGet.Search.Query";
    public const string PackageManagementPush = "NuGet.PackageManagement.Push";
    public const string PackageManagementPushSymbols = "NuGet.PackageManagement.PushSymbols";
    public const string PackageManagementList = "NuGet.PackageManagement.List";
    public const string PackageManagementUnlist = "NuGet.PackageManagement.Unlist";
    public const string PackageManagementRelist = "NuGet.PackageManagement.Relist";
    public const string PackageManagementDelete = "NuGet.PackageManagement.Delete";
    public const string ModerationModerate = "NuExtVault.Moderation.Moderate";
    public const string ModerationGetAudit = "NuExtVault.Moderation.GetAudit";
    public const string ModerationGetValidations = "NuExtVault.Moderation.GetValidations";
    public const string VulnerabilitiesGetIndex = "NuGet.Vulnerabilities.GetIndex";
    public const string VulnerabilitiesGetPage = "NuGet.Vulnerabilities.GetPage";
    public const string ControlGetState = "NuExtVault.Control.GetState";
    public const string ControlReset = "NuExtVault.Control.Reset";
    public const string ControlGetPackages = "NuExtVault.Control.GetPackages";
    public const string ControlAddPackage = "NuExtVault.Control.AddPackage";
    public const string ControlDeletePackage = "NuExtVault.Control.DeletePackage";
    public const string ControlRelistPackage = "NuExtVault.Control.RelistPackage";
    public const string ControlUnlistPackage = "NuExtVault.Control.UnlistPackage";
    public const string ControlUpdatePackageMetadata = "NuExtVault.Control.UpdatePackageMetadata";
    public const string ControlGetRequests = "NuExtVault.Control.GetRequests";
    public const string ControlClearRequests = "NuExtVault.Control.ClearRequests";
    public const string ControlGetFaults = "NuExtVault.Control.GetFaults";
    public const string ControlAddFault = "NuExtVault.Control.AddFault";
    public const string ControlClearFaults = "NuExtVault.Control.ClearFaults";
    public const string HealthGetLiveness = "NuExtVault.Health.GetLiveness";
    public const string HealthGetReadiness = "NuExtVault.Health.GetReadiness";
    public const string HealthGetStorage = "NuExtVault.Health.GetStorage";
    public const string DiagnosticsGet = "NuExtVault.Diagnostics.Get";
    public const string BackupCreate = "NuExtVault.Backup.Create";
    public const string RestoreExecute = "NuExtVault.Restore.Execute";
}
