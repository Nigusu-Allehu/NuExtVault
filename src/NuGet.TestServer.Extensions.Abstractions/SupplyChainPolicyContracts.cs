namespace NuGet.TestServer.Extensions.Abstractions;

internal static class SupplyChainPolicyPoints
{
    public const string Admission = "NuTest.SupplyChain.Admission";
    public const string Validation = "NuTest.SupplyChain.Validation";
    public const string Publication = Validation;
}

internal static class SupplyChainPolicyParticipantIds
{
    public const string Signature = "NuTest.SupplyChain.Signature";
    public const string Scanner = "NuTest.SupplyChain.Scanner";
    public const string Ownership = "NuTest.SupplyChain.Ownership";
    public const string Namespace = "NuTest.SupplyChain.Namespace";
    public const string Quota = "NuTest.SupplyChain.Quota";
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
