namespace NuGet.TestServer.Extensions.Sdk;

/// <summary>
/// The identities of the extensions that ship in the box. The kernel resolves profiles
/// and capability grants against these names; the official extension assembly declares
/// its manifests with them. Neither side needs a compile-time reference to the other.
/// </summary>
internal static class BuiltInExtensionIds
{
    public const string Protocol = "builtin.protocol";
    public const string ServiceIndex = "builtin.service-index";
    public const string Vulnerabilities = "builtin.vulnerabilities";
    public const string TestControl = "builtin.test-control";
    public const string DurableStorage = "builtin.durable-storage";
    public const string Operations = "builtin.operations";
    public const string SupplyChain = "builtin.supply-chain";
    public const string SupplyChainPolicy = "NuTest.SupplyChain";
}

/// <summary>
/// The canonical capability names. They are the single source of truth shared by code,
/// manifests, profiles, tests, and documentation on both sides of the assembly split.
/// </summary>
internal static class BuiltInCapabilityNames
{
    public const string PackagesIdentityRead = "packages.identity.read";
    public const string PackagesMetadataRead = "packages.metadata.read";
    public const string PackagesMetadataWrite = "packages.metadata.write";
    public const string PackagesContentRead = "packages.content.read";
    public const string PackagesSymbolsRead = "packages.symbols.read";
    public const string PackagesSearchQuery = KernelCapabilityNames.PackageSearchQuery;
    public const string PackagesContentWrite = "packages.content.write-staged";
    public const string PackagesPublish = "packages.publish";
    public const string PackagesUnlist = "packages.unlist";
    public const string PackagesRelist = "packages.relist";
    public const string PackagesDelete = "packages.delete";
    public const string ModerationRead = "moderation.read";
    public const string ModerationDecide = "moderation.decide";
    public const string VulnerabilityStateRead = "extension-state.vulnerabilities.read";
    public const string ExtensionStateRead = "extension-state.read";
    public const string ExtensionStateWrite = "extension-state.write";
    public const string EventsPublish = "events.publish";
    public const string BackupContribute = "backup.contribute";
    public const string BackupInvoke = "operations.backup.invoke";
    public const string RestoreInvoke = "operations.restore.invoke";
    public const string OperationsQuery = "operations.query";
    public const string ControlFaultsInject = "control.faults.inject";
    public const string ControlRequestsRead = "control.requests.read";
    public const string ControlPackagesManage = "control.packages.manage";
    public const string ControlInstrumentationManage = "control.instrumentation.manage";
    public const string DurableStorage = "storage.durable";
    public const string OutboundHttp = "network.outbound-http";
    public const string SecretsResolveReference = "secrets.resolve-reference";
    public const string SidecarExecution = "extensions.sidecar-execution";

    /// <summary>
    /// The narrow, read-only host clock any separately compiled module may request.
    /// </summary>
    public const string HostClockRead = KernelCapabilityNames.HostClockRead;
    public const string SupplyChainSignatureInspect =
        KernelCapabilityNames.SupplyChainSignatureInspect;
    public const string SupplyChainPackageScan = KernelCapabilityNames.SupplyChainPackageScan;
    public const string PackageContentWriteStaged =
        KernelCapabilityNames.PackageContentWriteStaged;
    public const string PublicationRequest = KernelCapabilityNames.PublicationRequest;
}
