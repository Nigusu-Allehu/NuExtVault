using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Hosting;

internal sealed record CapabilityGrant(string Name);

internal enum ServerProfileKind
{
    Embedded,
    Standard,
    Production
}

/// <summary>
/// A resolved host profile: which extensions the host selects and which capabilities it
/// grants them. The kernel validates a profile; the composition root decides its
/// content, so the kernel never names an official extension.
/// </summary>
internal sealed record ServerProfile(
    string Name,
    ServerProfileKind Kind,
    ImmutableArray<ExtensionSelection> Extensions,
    ImmutableArray<CapabilityGrant> Grants,
    ImmutableArray<ProfilePolicyRequirement> PolicyRequirements = default,
    ImmutableArray<OwnerIdentityMigrationAuthorization>
        OwnerIdentityMigrationAuthorizations = default);

internal sealed record OwnerIdentityMigrationAuthorization(
    string PredecessorId,
    string SuccessorExtensionId,
    string SuccessorPackageId,
    string ExpectedPublisher,
    string ExpectedSigningKeyId,
    string ExpectedSigningKeyFingerprint,
    string ExpectedPackageVersion,
    string ExpectedManifestDigest,
    string ExpectedStagedContentDigest);

internal sealed record ProfilePolicyRequirement(
    string PolicyPoint,
    ImmutableArray<string> RequiredAuthoritativeParticipants,
    int MinimumAuthoritativeParticipants);

/// <summary>
/// A temporary storage root owned by one host instance. It is released when the host is
/// disposed or when composition fails.
/// </summary>
internal sealed class TemporaryStorageLease : IDisposable
{
    private int _disposed;

    private TemporaryStorageLease(string path) => Path = path;

    public string Path { get; }

    public static TemporaryStorageLease Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NuExtVault",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryStorageLease(path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
