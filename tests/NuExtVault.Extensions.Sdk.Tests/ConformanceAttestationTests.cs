using System.Security.Cryptography;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Extensions.Sdk.Tests;

public sealed class ConformanceAttestationTests
{
    [Fact]
    public void Signed_attestation_round_trips_and_has_frozen_canonical_payload_bytes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload();
        var envelope = ConformanceAttestation.Sign(
            payload,
            key.ExportPkcs8PrivateKey(),
            "contoso-2026",
            IssuedAt,
            ExpiresAt,
            ConformanceAlgorithms.Es256);
        var expectedBytes = File.ReadAllBytes(
            TestPaths.Fixture("attestation-payload-v1.canonical.json"));

        Assert.Equal(expectedBytes, ConformanceAttestation.CanonicalPayloadBytes(payload).ToArray());
        Assert.Equal(expectedBytes, envelope.Payload.ToArray());
        Assert.Equal(ConformanceAlgorithms.Es256, envelope.Algorithm);
        Assert.Equal("contoso-2026", envelope.KeyId);
        Assert.True(Verify(envelope, key.ExportSubjectPublicKeyInfo()).IsValid);
    }

    [Theory]
    [InlineData("PackageId", "Other.Package", AttestationFailure.PackageIdentityMismatch)]
    [InlineData("PackageVersion", "9.9.9", AttestationFailure.PackageVersionMismatch)]
    [InlineData("Publisher", "OtherPublisher", AttestationFailure.PublisherMismatch)]
    [InlineData("ManifestSha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        AttestationFailure.ManifestMismatch)]
    [InlineData("Suite", "OtherSuite/v1", AttestationFailure.SuiteMismatch)]
    public void Verification_rejects_wrong_package_manifest_publisher_and_suite(
        string property,
        string value,
        AttestationFailure expectedFailure)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload();
        var envelope = Sign(payload, key);
        var expectation = Expectation() with
        {
            PackageId = property == "PackageId" ? value : payload.PackageId,
            PackageVersion = property == "PackageVersion" ? value : payload.PackageVersion,
            Publisher = property == "Publisher" ? value : payload.Publisher,
            ManifestSha256 = property == "ManifestSha256" ? value : payload.ManifestSha256,
            Suite = property == "Suite" ? value : payload.Suite
        };

        var result = ConformanceAttestationVerifier.Verify(
            envelope,
            expectation,
            [Root(key.ExportSubjectPublicKeyInfo())],
            VerificationTime);

        Assert.False(result.IsValid);
        Assert.Equal(expectedFailure, result.Failure);
    }

    [Theory]
    [InlineData("Sdk", AttestationFailure.SdkVersionMismatch)]
    [InlineData("Manifest", AttestationFailure.ManifestVersionMismatch)]
    [InlineData("Operation", AttestationFailure.OperationVersionMismatch)]
    [InlineData("Contribution", AttestationFailure.ContributionVersionMismatch)]
    [InlineData("Route", AttestationFailure.RouteVersionMismatch)]
    [InlineData("Capability", AttestationFailure.CapabilityVersionMismatch)]
    [InlineData("Structural", AttestationFailure.StructuralVersionMismatch)]
    public void Verification_rejects_every_independently_versioned_contract_mismatch(
        string contract,
        AttestationFailure expectedFailure)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(Payload(), key);
        var expected = Expectation() with
        {
            SdkVersion = contract == "Sdk" ? new SdkContractVersion(1, 1, 0) : Sdk,
            ManifestVersion = contract == "Manifest"
                ? new ManifestSchemaVersion(2)
                : new ManifestSchemaVersion(1),
            OperationVersion = contract == "Operation"
                ? new OperationContractVersion(2)
                : new OperationContractVersion(1),
            ContributionVersion = contract == "Contribution"
                ? new ContributionContractVersion(2)
                : new ContributionContractVersion(1),
            RouteVersion = contract == "Route"
                ? new RouteContractVersion(2)
                : new RouteContractVersion(1),
            CapabilityVersion = contract == "Capability"
                ? new CapabilityContractVersion(2)
                : new CapabilityContractVersion(1),
            StructuralVersion = contract == "Structural"
                ? new StructuralContractVersion(2)
                : new StructuralContractVersion(1)
        };

        var result = ConformanceAttestationVerifier.Verify(
            envelope,
            expected,
            [Root(key.ExportSubjectPublicKeyInfo())],
            VerificationTime);

        Assert.False(result.IsValid);
        Assert.Equal(expectedFailure, result.Failure);
    }

    [Fact]
    public void Verification_rejects_payload_and_signature_tampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(Payload(), key);
        var tamperedPayload = envelope.Payload.ToArray();
        tamperedPayload[^2] ^= 1;
        var tamperedSignature = envelope.Signature.ToArray();
        tamperedSignature[0] ^= 1;

        Assert.Equal(
            AttestationFailure.InvalidSignature,
            Verify(envelope with { Payload = tamperedPayload }, key.ExportSubjectPublicKeyInfo())
                .Failure);
        Assert.Equal(
            AttestationFailure.InvalidSignature,
            Verify(envelope with { Signature = tamperedSignature }, key.ExportSubjectPublicKeyInfo())
                .Failure);
    }

    [Fact]
    public void Verification_rejects_the_wrong_structural_digest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(Payload(), key);
        var expectation = Expectation() with
        {
            StructuralSha256 =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        var result = ConformanceAttestationVerifier.Verify(
            envelope,
            expectation,
            [Root(key.ExportSubjectPublicKeyInfo())],
            VerificationTime);

        Assert.False(result.IsValid);
        Assert.Equal(AttestationFailure.StructuralIdentityMismatch, result.Failure);
    }

    [Fact]
    public void Verification_rejects_wrong_key_id_and_key_material()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(Payload(), signingKey);

        Assert.Equal(
            AttestationFailure.UntrustedKey,
            Verify(envelope with { KeyId = "unknown" }, signingKey.ExportSubjectPublicKeyInfo())
                .Failure);
        Assert.Equal(
            AttestationFailure.InvalidSignature,
            Verify(envelope, otherKey.ExportSubjectPublicKeyInfo()).Failure);
    }

    [Fact]
    public void Verification_rejects_expiry_algorithm_mismatch_and_missing_trust_root_fail_closed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(Payload(), key);

        Assert.Equal(
            AttestationFailure.Expired,
            ConformanceAttestationVerifier.Verify(
                envelope,
                Expectation(),
                [Root(key.ExportSubjectPublicKeyInfo())],
                ExpiresAt).Failure);
        Assert.Equal(
            AttestationFailure.AlgorithmMismatch,
            ConformanceAttestationVerifier.Verify(
                envelope with { Algorithm = "RS256" },
                Expectation(),
                [Root(key.ExportSubjectPublicKeyInfo())],
                VerificationTime).Failure);
        Assert.Equal(
            AttestationFailure.TrustRootMissing,
            ConformanceAttestationVerifier.Verify(
                envelope,
                Expectation(),
                [],
                VerificationTime).Failure);
    }

    private static readonly DateTimeOffset IssuedAt =
        DateTimeOffset.Parse("2026-08-21T00:00:00Z");

    private static readonly DateTimeOffset ExpiresAt =
        DateTimeOffset.Parse("2026-09-21T00:00:00Z");

    private static readonly DateTimeOffset VerificationTime =
        DateTimeOffset.Parse("2026-08-22T00:00:00Z");

    private static readonly SdkContractVersion Sdk = new(1, 2, 0);

    private static ConformanceAttestationPayload Payload() => new(
        PayloadVersion: 1,
        PackageId: "Contoso.Flavors",
        PackageVersion: "1.2.3",
        Publisher: "Contoso",
        ManifestSha256: File.ReadAllText(
            TestPaths.Fixture("valid-v1.canonical.sha256")).Trim(),
        SdkVersion: Sdk,
        ManifestVersion: new ManifestSchemaVersion(1),
        OperationVersion: new OperationContractVersion(1),
        ContributionVersion: new ContributionContractVersion(1),
        RouteVersion: new RouteContractVersion(1),
        CapabilityVersion: new CapabilityContractVersion(1),
        StructuralVersion: new StructuralContractVersion(1),
        StructuralSha256: File.ReadAllText(
            TestPaths.Snapshot("sdk-v1.structural-contract.sha256")).Trim(),
        Suite: ExtensionSdkVersions.ConformanceSuiteV1);

    private static ConformanceExpectation Expectation() => new(
        PackageId: Payload().PackageId,
        PackageVersion: Payload().PackageVersion,
        Publisher: Payload().Publisher,
        ManifestSha256: Payload().ManifestSha256,
        SdkVersion: Payload().SdkVersion,
        ManifestVersion: Payload().ManifestVersion,
        OperationVersion: Payload().OperationVersion,
        ContributionVersion: Payload().ContributionVersion,
        RouteVersion: Payload().RouteVersion,
        CapabilityVersion: Payload().CapabilityVersion,
        StructuralVersion: Payload().StructuralVersion,
        StructuralSha256: Payload().StructuralSha256,
        Suite: Payload().Suite);

    private static ConformanceTrustRoot Root(byte[] publicKey) => new(
        Publisher: "Contoso",
        KeyId: "contoso-2026",
        Algorithm: ConformanceAlgorithms.Es256,
        SubjectPublicKeyInfo: publicKey);

    private static ConformanceAttestationEnvelope Sign(
        ConformanceAttestationPayload payload,
        ECDsa key) =>
        ConformanceAttestation.Sign(
            payload,
            key.ExportPkcs8PrivateKey(),
            "contoso-2026",
            IssuedAt,
            ExpiresAt,
            ConformanceAlgorithms.Es256);

    private static AttestationVerificationResult Verify(
        ConformanceAttestationEnvelope envelope,
        byte[] publicKey) =>
        ConformanceAttestationVerifier.Verify(
            envelope,
            Expectation(),
            [Root(publicKey)],
            VerificationTime);
}
