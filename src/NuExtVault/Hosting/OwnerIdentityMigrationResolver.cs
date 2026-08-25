using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Kernel.Capabilities;

namespace NuExtVault.Hosting;

internal static class OwnerIdentityMigrationResolver
{
    public static ImmutableArray<OwnerIdentityMigration> Resolve(
        IEnumerable<IExtensionModule> modules,
        IEnumerable<string> activeExtensionIds,
        IEnumerable<OwnerIdentityMigrationAuthorization> authorizations)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(activeExtensionIds);
        ArgumentNullException.ThrowIfNull(authorizations);
        var active = activeExtensionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configured = authorizations.Select(Validate).ToArray();
        var duplicate = configured
            .GroupBy(item => item.PredecessorId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw Failure(
                $"Durable identity predecessor '{duplicate.Key}' has duplicate or ambiguous " +
                "administrator authorizations.");
        }

        var declarations = modules
            .Select(module => module.Contribution.Manifest)
            .Where(manifest =>
                active.Contains(manifest.Identity.Id) &&
                !manifest.IdentityPredecessors.IsDefaultOrEmpty)
            .SelectMany(manifest => manifest.IdentityPredecessors.Select(
                predecessor => (Manifest: manifest, Predecessor: predecessor)))
            .ToArray();
        var migrations = ImmutableArray.CreateBuilder<OwnerIdentityMigration>();
        var used = new HashSet<OwnerIdentityMigrationAuthorization>();
        foreach (var declaration in declarations)
        {
            if (active.Contains(declaration.Predecessor))
            {
                throw Failure(
                    $"Active extension '{declaration.Predecessor}' cannot be migrated to " +
                    $"'{declaration.Manifest.Identity.Id}'.");
            }

            var authorization = configured.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.PredecessorId,
                    declaration.Predecessor,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.SuccessorExtensionId,
                    declaration.Manifest.Identity.Id,
                    StringComparison.Ordinal));
            if (authorization is null)
            {
                throw Failure(
                    $"Extension '{declaration.Manifest.Identity.Id}' declares durable identity " +
                    $"predecessor '{declaration.Predecessor}' without explicit administrator " +
                    "authorization.");
            }

            ValidateVerifiedIdentity(declaration.Manifest, authorization);
            used.Add(authorization);
            migrations.Add(new OwnerIdentityMigration(
                declaration.Predecessor,
                declaration.Manifest.Identity.Id,
                ComputeDigest(authorization, declaration.Manifest)));
        }

        var unused = configured.FirstOrDefault(authorization => !used.Contains(authorization));
        if (unused is not null)
        {
            throw Failure(
                $"Administrator authorization from '{unused.PredecessorId}' to " +
                $"'{unused.SuccessorExtensionId}' does not match an active signed manifest " +
                "declaration.");
        }

        return migrations.ToImmutable();
    }

    private static OwnerIdentityMigrationAuthorization Validate(
        OwnerIdentityMigrationAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (string.IsNullOrWhiteSpace(authorization.PredecessorId) ||
            string.IsNullOrWhiteSpace(authorization.SuccessorExtensionId) ||
            string.IsNullOrWhiteSpace(authorization.SuccessorPackageId) ||
            string.IsNullOrWhiteSpace(authorization.ExpectedPublisher) ||
            string.IsNullOrWhiteSpace(authorization.ExpectedSigningKeyId) ||
            !IsSha256(authorization.ExpectedSigningKeyFingerprint) ||
            string.IsNullOrWhiteSpace(authorization.ExpectedPackageVersion) ||
            !IsSha256(authorization.ExpectedManifestDigest) ||
            !IsSha256(authorization.ExpectedStagedContentDigest))
        {
            throw Failure("Durable identity migration administrator authorization is invalid.");
        }
        if (string.Equals(
                authorization.PredecessorId,
                authorization.SuccessorExtensionId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("Durable identity migration administrator authorization is a self-link.");
        }

        return authorization;
    }

    private static void ValidateVerifiedIdentity(
        ExtensionManifest manifest,
        OwnerIdentityMigrationAuthorization authorization)
    {
        if (manifest.ValidatedManifestDigest is null ||
            manifest.ValidatedStagedContentDigest is null ||
            manifest.ValidatedPackageId is null ||
            manifest.ValidatedPackageVersion is null ||
            manifest.ValidatedPublisher is null ||
            manifest.ValidatedSigningKeyId is null ||
            manifest.ValidatedSigningKeyFingerprint is null)
        {
            throw Failure(
                $"Extension '{manifest.Identity.Id}' cannot migrate durable identity because its " +
                "successor package, manifest, and signing key were not verified.");
        }

        if (!string.Equals(
                authorization.SuccessorPackageId,
                manifest.ValidatedPackageId,
                StringComparison.Ordinal) ||
            !string.Equals(
                authorization.ExpectedPublisher,
                manifest.ValidatedPublisher,
                StringComparison.Ordinal) ||
            !string.Equals(
                authorization.ExpectedSigningKeyId,
                manifest.ValidatedSigningKeyId,
                StringComparison.Ordinal) ||
            !string.Equals(
                authorization.ExpectedSigningKeyFingerprint,
                manifest.ValidatedSigningKeyFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                authorization.ExpectedPackageVersion,
                manifest.ValidatedPackageVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                authorization.ExpectedManifestDigest,
                manifest.ValidatedManifestDigest,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                authorization.ExpectedStagedContentDigest,
                manifest.ValidatedStagedContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                $"Administrator authorization for predecessor '{authorization.PredecessorId}' " +
                $"does not match verified successor package '{manifest.ValidatedPackageId}'.");
        }
    }

    private static string ComputeDigest(
        OwnerIdentityMigrationAuthorization authorization,
        ExtensionManifest manifest)
    {
        var fields = new[]
        {
            authorization.PredecessorId,
            authorization.SuccessorExtensionId,
            authorization.SuccessorPackageId,
            authorization.ExpectedPublisher,
            authorization.ExpectedSigningKeyId,
            authorization.ExpectedSigningKeyFingerprint.ToLowerInvariant(),
            authorization.ExpectedPackageVersion,
            authorization.ExpectedManifestDigest.ToLowerInvariant(),
            authorization.ExpectedStagedContentDigest.ToLowerInvariant()
        };
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static ServerHostingConfigurationException Failure(string message) => new(message);
}
