namespace NuGet.TestServer.Extensions.Sdk;

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
    public const string ModerationModerate = "NuTest.Moderation.Moderate";
    public const string ModerationGetAudit = "NuTest.Moderation.GetAudit";
    public const string ModerationGetValidations = "NuTest.Moderation.GetValidations";
    public const string VulnerabilitiesGetIndex = "NuGet.Vulnerabilities.GetIndex";
    public const string VulnerabilitiesGetPage = "NuGet.Vulnerabilities.GetPage";
    public const string ControlGetState = "NuTest.Control.GetState";
    public const string ControlReset = "NuTest.Control.Reset";
    public const string ControlGetPackages = "NuTest.Control.GetPackages";
    public const string ControlAddPackage = "NuTest.Control.AddPackage";
    public const string ControlDeletePackage = "NuTest.Control.DeletePackage";
    public const string ControlRelistPackage = "NuTest.Control.RelistPackage";
    public const string ControlUnlistPackage = "NuTest.Control.UnlistPackage";
    public const string ControlUpdatePackageMetadata = "NuTest.Control.UpdatePackageMetadata";
    public const string ControlGetRequests = "NuTest.Control.GetRequests";
    public const string ControlClearRequests = "NuTest.Control.ClearRequests";
    public const string ControlGetFaults = "NuTest.Control.GetFaults";
    public const string ControlAddFault = "NuTest.Control.AddFault";
    public const string ControlClearFaults = "NuTest.Control.ClearFaults";
    public const string HealthGetLiveness = "NuTest.Health.GetLiveness";
    public const string HealthGetReadiness = "NuTest.Health.GetReadiness";
    public const string HealthGetStorage = "NuTest.Health.GetStorage";
    public const string DiagnosticsGet = "NuTest.Diagnostics.Get";
    public const string BackupCreate = "NuTest.Backup.Create";
    public const string RestoreExecute = "NuTest.Restore.Execute";
}
