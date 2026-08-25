using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NuExtVault.Extensions.Sdk;

public static class ConformanceAlgorithms
{
    public const string Es256 = "ES256";
}

public sealed record ConformanceAttestationPayload(
    int PayloadVersion,
    string PackageId,
    string PackageVersion,
    string Publisher,
    string ManifestSha256,
    SdkContractVersion SdkVersion,
    ManifestSchemaVersion ManifestVersion,
    OperationContractVersion OperationVersion,
    ContributionContractVersion ContributionVersion,
    RouteContractVersion RouteVersion,
    CapabilityContractVersion CapabilityVersion,
    StructuralContractVersion StructuralVersion,
    string StructuralSha256,
    string Suite);

public sealed record ConformanceAttestationEnvelope(
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> Signature,
    string Algorithm,
    string KeyId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record ConformanceTrustRoot(
    string Publisher,
    string KeyId,
    string Algorithm,
    ReadOnlyMemory<byte> SubjectPublicKeyInfo);

public sealed record ConformanceExpectation(
    string PackageId,
    string PackageVersion,
    string Publisher,
    string ManifestSha256,
    SdkContractVersion SdkVersion,
    ManifestSchemaVersion ManifestVersion,
    OperationContractVersion OperationVersion,
    ContributionContractVersion ContributionVersion,
    RouteContractVersion RouteVersion,
    CapabilityContractVersion CapabilityVersion,
    StructuralContractVersion StructuralVersion,
    string StructuralSha256,
    string Suite);

public enum AttestationFailure
{
    None,
    TrustRootMissing,
    AlgorithmMismatch,
    UntrustedKey,
    InvalidSignature,
    NotYetValid,
    Expired,
    PayloadInvalid,
    PackageIdentityMismatch,
    PackageVersionMismatch,
    PublisherMismatch,
    ManifestMismatch,
    SdkVersionMismatch,
    ManifestVersionMismatch,
    OperationVersionMismatch,
    ContributionVersionMismatch,
    RouteVersionMismatch,
    CapabilityVersionMismatch,
    StructuralVersionMismatch,
    StructuralIdentityMismatch,
    SuiteMismatch
}

public readonly record struct AttestationVerificationResult(
    bool IsValid,
    AttestationFailure Failure);

public static class ConformanceAttestation
{
    public static ConformanceAttestationEnvelope Sign(
        ConformanceAttestationPayload payload,
        ReadOnlyMemory<byte> privateKey,
        string keyId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string algorithm)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (!string.Equals(algorithm, ConformanceAlgorithms.Es256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only ES256 attestations are supported.", nameof(algorithm));
        }
        if (expiresAt <= issuedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                "An attestation must expire after it is issued.");
        }

        var canonicalPayload = CanonicalPayloadBytes(payload);
        var signingInput = AttestationSigningInput.Create(
            canonicalPayload.Span,
            algorithm,
            keyId,
            issuedAt,
            expiresAt);
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(privateKey.Span, out var bytesRead);
        if (bytesRead != privateKey.Length || signer.KeySize != 256)
        {
            throw new CryptographicException("The attestation key must be an ECDSA P-256 key.");
        }

        var signature = signer.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new ConformanceAttestationEnvelope(
            canonicalPayload,
            signature,
            algorithm,
            keyId,
            issuedAt,
            expiresAt);
    }

    public static ReadOnlyMemory<byte> CanonicalPayloadBytes(
        ConformanceAttestationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("capabilityVersion", payload.CapabilityVersion.Value);
            writer.WriteNumber("contributionVersion", payload.ContributionVersion.Value);
            writer.WriteString("manifestSha256", payload.ManifestSha256);
            writer.WriteNumber("manifestVersion", payload.ManifestVersion.Value);
            writer.WriteNumber("operationVersion", payload.OperationVersion.Value);
            writer.WriteString("packageId", payload.PackageId);
            writer.WriteString("packageVersion", payload.PackageVersion);
            writer.WriteNumber("payloadVersion", payload.PayloadVersion);
            writer.WriteString("publisher", payload.Publisher);
            writer.WriteNumber("routeVersion", payload.RouteVersion.Value);
            writer.WriteString("sdkVersion", payload.SdkVersion.ToString());
            writer.WriteString("structuralSha256", payload.StructuralSha256);
            writer.WriteNumber("structuralVersion", payload.StructuralVersion.Value);
            writer.WriteString("suite", payload.Suite);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

public static class ConformanceAttestationVerifier
{
    public static AttestationVerificationResult Verify(
        ConformanceAttestationEnvelope envelope,
        ConformanceExpectation expectation,
        IEnumerable<ConformanceTrustRoot> trustRoots,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(trustRoots);
        var roots = trustRoots.ToArray();
        if (roots.Length == 0)
        {
            return Invalid(AttestationFailure.TrustRootMissing);
        }
        if (!string.Equals(
                envelope.Algorithm,
                ConformanceAlgorithms.Es256,
                StringComparison.Ordinal))
        {
            return Invalid(AttestationFailure.AlgorithmMismatch);
        }

        var matchingRoots = roots.Where(root =>
                string.Equals(root.KeyId, envelope.KeyId, StringComparison.Ordinal) &&
                string.Equals(root.Algorithm, envelope.Algorithm, StringComparison.Ordinal))
            .ToArray();
        if (matchingRoots.Length == 0)
        {
            return Invalid(AttestationFailure.UntrustedKey);
        }

        var signingInput = AttestationSigningInput.Create(
            envelope.Payload.Span,
            envelope.Algorithm,
            envelope.KeyId,
            envelope.IssuedAt,
            envelope.ExpiresAt);
        var verifiedRoot = matchingRoots.FirstOrDefault(root =>
            VerifySignature(root.SubjectPublicKeyInfo.Span, signingInput, envelope.Signature.Span));
        if (verifiedRoot is null)
        {
            return Invalid(AttestationFailure.InvalidSignature);
        }

        if (now < envelope.IssuedAt)
        {
            return Invalid(AttestationFailure.NotYetValid);
        }
        if (now >= envelope.ExpiresAt)
        {
            return Invalid(AttestationFailure.Expired);
        }

        if (!TryParsePayload(envelope.Payload.Span, out var payload))
        {
            return Invalid(AttestationFailure.PayloadInvalid);
        }
        if (!string.Equals(
                verifiedRoot.Publisher,
                payload.Publisher,
                StringComparison.Ordinal))
        {
            return Invalid(AttestationFailure.UntrustedKey);
        }

        if (!string.Equals(payload.PackageId, expectation.PackageId, StringComparison.Ordinal))
        {
            return Invalid(AttestationFailure.PackageIdentityMismatch);
        }
        if (!string.Equals(
                payload.PackageVersion,
                expectation.PackageVersion,
                StringComparison.Ordinal))
        {
            return Invalid(AttestationFailure.PackageVersionMismatch);
        }
        if (!string.Equals(payload.Publisher, expectation.Publisher, StringComparison.Ordinal))
        {
            return Invalid(AttestationFailure.PublisherMismatch);
        }
        if (!FixedHexEquals(payload.ManifestSha256, expectation.ManifestSha256))
        {
            return Invalid(AttestationFailure.ManifestMismatch);
        }
        if (payload.SdkVersion != expectation.SdkVersion)
        {
            return Invalid(AttestationFailure.SdkVersionMismatch);
        }
        if (payload.ManifestVersion != expectation.ManifestVersion)
        {
            return Invalid(AttestationFailure.ManifestVersionMismatch);
        }
        if (payload.OperationVersion != expectation.OperationVersion)
        {
            return Invalid(AttestationFailure.OperationVersionMismatch);
        }
        if (payload.ContributionVersion != expectation.ContributionVersion)
        {
            return Invalid(AttestationFailure.ContributionVersionMismatch);
        }
        if (payload.RouteVersion != expectation.RouteVersion)
        {
            return Invalid(AttestationFailure.RouteVersionMismatch);
        }
        if (payload.CapabilityVersion != expectation.CapabilityVersion)
        {
            return Invalid(AttestationFailure.CapabilityVersionMismatch);
        }
        if (payload.StructuralVersion != expectation.StructuralVersion)
        {
            return Invalid(AttestationFailure.StructuralVersionMismatch);
        }
        if (!FixedHexEquals(payload.StructuralSha256, expectation.StructuralSha256))
        {
            return Invalid(AttestationFailure.StructuralIdentityMismatch);
        }
        if (!string.Equals(payload.Suite, expectation.Suite, StringComparison.Ordinal))
        {
            return Invalid(AttestationFailure.SuiteMismatch);
        }

        return new AttestationVerificationResult(true, AttestationFailure.None);
    }

    private static bool VerifySignature(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> signingInput,
        ReadOnlySpan<byte> signature)
    {
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            return bytesRead == publicKey.Length &&
                   verifier.KeySize == 256 &&
                   verifier.VerifyData(
                       signingInput,
                       signature,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryParsePayload(
        ReadOnlySpan<byte> bytes,
        out ConformanceAttestationPayload payload)
    {
        payload = null!;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("payloadVersion").GetInt32() != 1 ||
                !SdkContractVersion.TryParse(
                    root.GetProperty("sdkVersion").GetString(),
                    out var sdkVersion))
            {
                return false;
            }

            payload = new ConformanceAttestationPayload(
                root.GetProperty("payloadVersion").GetInt32(),
                root.GetProperty("packageId").GetString()!,
                root.GetProperty("packageVersion").GetString()!,
                root.GetProperty("publisher").GetString()!,
                root.GetProperty("manifestSha256").GetString()!,
                sdkVersion,
                new ManifestSchemaVersion(root.GetProperty("manifestVersion").GetInt32()),
                new OperationContractVersion(root.GetProperty("operationVersion").GetInt32()),
                new ContributionContractVersion(
                    root.GetProperty("contributionVersion").GetInt32()),
                new RouteContractVersion(root.GetProperty("routeVersion").GetInt32()),
                new CapabilityContractVersion(
                    root.GetProperty("capabilityVersion").GetInt32()),
                new StructuralContractVersion(
                    root.GetProperty("structuralVersion").GetInt32()),
                root.GetProperty("structuralSha256").GetString()!,
                root.GetProperty("suite").GetString()!);
            return ConformanceAttestation.CanonicalPayloadBytes(payload).Span.SequenceEqual(bytes);
        }
        catch (Exception exception) when (
            exception is JsonException or
            KeyNotFoundException or
            InvalidOperationException or
            FormatException)
        {
            return false;
        }
    }

    private static bool FixedHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64)
        {
            return false;
        }

        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static AttestationVerificationResult Invalid(AttestationFailure failure) =>
        new(false, failure);
}

internal static class AttestationSigningInput
{
    private static readonly byte[] Domain =
        "NuExtVault.Extensions.Conformance.Attestation/v1"u8.ToArray();

    internal static byte[] Create(
        ReadOnlySpan<byte> payload,
        string algorithm,
        string keyId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var algorithmBytes = Encoding.UTF8.GetBytes(algorithm);
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        var issuedBytes = Encoding.UTF8.GetBytes(issuedAt.ToUniversalTime().ToString("O"));
        var expiresBytes = Encoding.UTF8.GetBytes(expiresAt.ToUniversalTime().ToString("O"));
        var length = Domain.Length + 6 * sizeof(int) +
                     algorithmBytes.Length +
                     keyIdBytes.Length +
                     issuedBytes.Length +
                     expiresBytes.Length +
                     payload.Length;
        var output = new byte[length];
        var offset = 0;
        Write(Domain);
        Write(algorithmBytes);
        Write(keyIdBytes);
        Write(issuedBytes);
        Write(expiresBytes);
        Write(payload);
        return output;

        void Write(ReadOnlySpan<byte> value)
        {
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(offset, sizeof(int)), value.Length);
            offset += sizeof(int);
            value.CopyTo(output.AsSpan(offset));
            offset += value.Length;
        }
    }
}
