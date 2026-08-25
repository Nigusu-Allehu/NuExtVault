namespace NuExtVault.Extensions.Sdk;

internal static class SupplyChainPolicyPoints
{
    public const string Admission = "NuExtVault.SupplyChain.Admission";
    public const string Validation = "NuExtVault.SupplyChain.Validation";
    public const string Publication = Validation;
}

internal static class SupplyChainPolicyParticipantIds
{
    public const string Signature = "NuExtVault.SupplyChain.Signature";
    public const string Scanner = "NuExtVault.SupplyChain.Scanner";
    public const string Ownership = "NuExtVault.SupplyChain.Ownership";
    public const string Namespace = "NuExtVault.SupplyChain.Namespace";
    public const string Quota = "NuExtVault.SupplyChain.Quota";
}

internal sealed record SupplyChainPolicyContext(
    PolicyPackageHandle Package,
    PolicyPackageIdentity Identity,
    long ContentLength,
    string IdentityName,
    string Repository,
    bool Administrator,
    bool RequireSignedPackage,
    string? ExistingOwner,
    bool IdentityOwnsPackage,
    bool NamespaceAllowed,
    bool QuotaAvailable);
