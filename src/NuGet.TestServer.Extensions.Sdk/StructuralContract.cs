using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NuGet.TestServer.Extensions.Sdk;

public sealed class StructuralContractIdentity : IEquatable<StructuralContractIdentity>
{
    internal StructuralContractIdentity(
        StructuralContractVersion version,
        ReadOnlyMemory<byte> canonicalBytes,
        string sha256)
    {
        Version = version;
        CanonicalBytes = canonicalBytes;
        Sha256 = sha256;
    }

    public StructuralContractVersion Version { get; }

    public ReadOnlyMemory<byte> CanonicalBytes { get; }

    public string Sha256 { get; }

    public bool Equals(StructuralContractIdentity? other) =>
        other is not null &&
        Version == other.Version &&
        Sha256 == other.Sha256 &&
        CanonicalBytes.Span.SequenceEqual(other.CanonicalBytes.Span);

    public override bool Equals(object? obj) =>
        obj is StructuralContractIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Version, Sha256);
}

public static class StructuralContractFingerprint
{
    private const string V1Contract =
        "contract=NuGet.TestServer.Extensions.Sdk/v1\r\n" +
        "identity=manifest:1\r\n" +
        "identity=operation:1\r\n" +
        "identity=contribution:1\r\n" +
        "identity=route:1\r\n" +
        "identity=capability:1\r\n" +
        "identity=structural:1\r\n" +
        "replacement=disabled\r\n" +
        "target-framework=net10.0\r\n";

    private static readonly ReadOnlyMemory<byte> V1Bytes = Encoding.UTF8.GetBytes(V1Contract);
    private static readonly string V1Sha256 =
        Convert.ToHexStringLower(SHA256.HashData(V1Bytes.Span));
    private static readonly StructuralContractIdentity V1 = new(
        ExtensionSdkVersions.StructuralV1,
        V1Bytes,
        V1Sha256);

    public static StructuralContractIdentity Create(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!string.Equals(
                assembly.GetName().Name,
                ExtensionSdkVersions.Identity.Value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Structural SDK identity can be computed only for the SDK assembly.",
                nameof(assembly));
        }

        return V1;
    }
}
