using System.Text;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Extensions.TestKit;

namespace NuGet.TestServer.Extensions.Sdk.Tests;

public sealed class CanonicalIdentityTests
{
    [Fact]
    public void Manifest_fingerprint_and_attestation_share_the_same_canonical_byte_definitions()
    {
        var manifest = ExtensionManifestJson.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json")));
        var canonical = ExtensionManifestJson.Canonicalize(manifest);
        var identity = StructuralContractFingerprint.Create(typeof(IExtensionModule).Assembly);

        Assert.Equal(
            canonical.ToArray(),
            CanonicalContractBytes.Manifest(manifest).ToArray());
        Assert.Equal(
            ExtensionManifestJson.ComputeDigest(canonical),
            CanonicalContractBytes.ManifestDigest(manifest));
        Assert.Equal(
            identity.CanonicalBytes.ToArray(),
            CanonicalContractBytes.StructuralContract(typeof(IExtensionModule).Assembly).ToArray());
    }

    [Fact]
    public void Structural_contract_matches_the_reviewed_golden_and_digest()
    {
        var fingerprint = StructuralContractFingerprint.Create(typeof(IExtensionModule).Assembly);
        var golden = Encoding.UTF8.GetBytes(
            File.ReadAllText(TestPaths.Snapshot("sdk-v1.structural-contract.txt"))
                .ReplaceLineEndings("\n"));

        Assert.Equal(new StructuralContractVersion(1), fingerprint.Version);
        Assert.Equal(golden, fingerprint.CanonicalBytes.ToArray());
        Assert.Equal(TestPaths.Sha256(golden), fingerprint.Sha256);
        Assert.Equal(
            File.ReadAllText(TestPaths.Snapshot("sdk-v1.structural-contract.sha256")).Trim(),
            fingerprint.Sha256);
    }

    [Fact]
    public void Fingerprints_and_validation_order_are_deterministic()
    {
        var first = StructuralContractFingerprint.Create(typeof(IExtensionModule).Assembly);
        var second = StructuralContractFingerprint.Create(typeof(IExtensionModule).Assembly);

        Assert.Equal(first, second);
        Assert.Equal(first.CanonicalBytes.ToArray(), second.CanonicalBytes.ToArray());
        Assert.Equal(first.Sha256, second.Sha256);
    }
}
