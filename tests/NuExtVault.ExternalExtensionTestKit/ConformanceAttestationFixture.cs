using System.Security.Cryptography;
using System.Text.Json;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.ExternalExtensionTestKit;

/// <summary>
/// Step 20 tests-first red phase helper. Builds ES256 trust roots and signed
/// <c>extension-attestation.json</c> content on top of the already-implemented
/// (Step 19) <see cref="ConformanceAttestation"/> primitives, so every negative
/// attestation test (tampered, expired, wrong identity/publisher/key/manifest/
/// contract/suite) can be produced from one small, deterministic surface.
/// </summary>
public static class ConformanceAttestationFixture
{
    public const string DefaultKeyId = "contoso-test-2026";
    public const string DefaultPublisher = "Contoso";

    public static readonly DateTimeOffset DefaultIssuedAt =
        DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    public static readonly DateTimeOffset DefaultExpiresAt =
        DateTimeOffset.Parse("2027-08-01T00:00:00Z");

    /// <summary>Creates a fresh ES256 key pair and the trust root the host would be
    /// configured with to trust attestations signed by that key.</summary>
    public static (ECDsa Key, ConformanceTrustRoot TrustRoot) CreateTrustedKey(
        string publisher = DefaultPublisher,
        string keyId = DefaultKeyId)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trustRoot = new ConformanceTrustRoot(
            publisher,
            keyId,
            ConformanceAlgorithms.Es256,
            key.ExportSubjectPublicKeyInfo());
        return (key, trustRoot);
    }

    public static ConformanceAttestationPayload BuildPayload(
        string packageId,
        string packageVersion,
        string publisher,
        string manifestSha256,
        string structuralSha256,
        SdkContractVersion? sdkVersion = null,
        string? suite = null,
        ManifestSchemaVersion? manifestVersion = null) =>
        new(
            PayloadVersion: 1,
            PackageId: packageId,
            PackageVersion: packageVersion,
            Publisher: publisher,
            ManifestSha256: manifestSha256,
            SdkVersion: sdkVersion ?? ExtensionSdkVersions.Current,
            ManifestVersion: manifestVersion ?? ExtensionSdkVersions.ManifestV1,
            OperationVersion: ExtensionSdkVersions.OperationV1,
            ContributionVersion: ExtensionSdkVersions.ContributionV1,
            RouteVersion: ExtensionSdkVersions.RouteV1,
            CapabilityVersion: ExtensionSdkVersions.CapabilityV1,
            StructuralVersion: ExtensionSdkVersions.StructuralV1,
            StructuralSha256: structuralSha256,
            Suite: suite ?? ExtensionSdkVersions.ConformanceSuiteV1);

    /// <summary>Signs <paramref name="payload"/> and serializes the envelope into the
    /// assumed <c>extension-attestation.json</c> shape (see
    /// .design/microkernel-step20-external-extension-tests.md).</summary>
    public static byte[] SignToAttestationJson(
        ConformanceAttestationPayload payload,
        ECDsa key,
        string? keyId = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        string algorithm = ConformanceAlgorithms.Es256)
    {
        var envelope = ConformanceAttestation.Sign(
            payload,
            key.ExportPkcs8PrivateKey(),
            keyId ?? DefaultKeyId,
            issuedAt ?? DefaultIssuedAt,
            expiresAt ?? DefaultExpiresAt,
            algorithm);
        return EnvelopeToJson(envelope);
    }

    public static byte[] EnvelopeToJson(ConformanceAttestationEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(new AttestationFileModel(
            Convert.ToBase64String(envelope.Payload.Span),
            Convert.ToBase64String(envelope.Signature.Span),
            envelope.Algorithm,
            envelope.KeyId,
            envelope.IssuedAt,
            envelope.ExpiresAt),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    /// <summary>Corrupts one byte of a signed attestation's payload, producing a
    /// tampered-but-well-formed <c>extension-attestation.json</c>.</summary>
    public static byte[] Tamper(byte[] attestationJson)
    {
        using var document = JsonDocument.Parse(attestationJson);
        var payloadBytes = Convert.FromBase64String(
            document.RootElement.GetProperty("payloadBase64").GetString()!);
        payloadBytes[^1] ^= 0x01;
        return JsonSerializer.SerializeToUtf8Bytes(new AttestationFileModel(
            Convert.ToBase64String(payloadBytes),
            document.RootElement.GetProperty("signatureBase64").GetString()!,
            document.RootElement.GetProperty("algorithm").GetString()!,
            document.RootElement.GetProperty("keyId").GetString()!,
            document.RootElement.GetProperty("issuedAt").GetDateTimeOffset(),
            document.RootElement.GetProperty("expiresAt").GetDateTimeOffset()),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private sealed record AttestationFileModel(
        string PayloadBase64,
        string SignatureBase64,
        string Algorithm,
        string KeyId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);
}
