using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NuExtVault.ExternalExtensionTestKit;

/// <summary>
/// Step 20 tests-first red phase helper. Builds and mutates the raw byte content
/// of `.nupkg` archives that the (not yet implemented) external extension
/// package loader is expected to discover in configured roots. See
/// .design/microkernel-step20-external-extension-tests.md for the assumed
/// on-disk layout.
/// </summary>
public static class ExternalExtensionPackageBuilder
{
    public const string ManifestEntryName = "extension-manifest.json";
    public const string PackageEntryName = "extension-package.json";
    public const string AttestationEntryName = "extension-attestation.json";
    public const string LibDirectory = "lib/net10.0/";

    /// <summary>Builds a `.nupkg` from scratch. Any component may be omitted to
    /// exercise a missing-file negative case.</summary>
    public static byte[] BuildNupkg(
        string id,
        string version,
        byte[]? manifestJsonBytes = null,
        byte[]? packageJsonBytes = null,
        byte[]? attestationJsonBytes = null,
        IReadOnlyDictionary<string, byte[]>? libFiles = null,
        IReadOnlyDictionary<string, byte[]>? extraEntries = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, $"{id}.nuspec", Encoding.UTF8.GetBytes(BuildNuspec(id, version)));
            if (manifestJsonBytes is not null)
            {
                WriteEntry(archive, ManifestEntryName, manifestJsonBytes);
            }

            if (packageJsonBytes is not null)
            {
                WriteEntry(archive, PackageEntryName, packageJsonBytes);
            }

            if (attestationJsonBytes is not null)
            {
                WriteEntry(archive, AttestationEntryName, attestationJsonBytes);
            }

            foreach (var (name, content) in libFiles ?? new Dictionary<string, byte[]>())
            {
                WriteEntry(archive, $"{LibDirectory}{name}", content);
            }

            foreach (var (name, content) in extraEntries ?? new Dictionary<string, byte[]>())
            {
                WriteEntry(archive, name, content);
            }
        }

        return stream.ToArray();
    }

    public static string BuildNuspec(string id, string version, string authors = "Test") =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
           <metadata>
             <id>{id}</id>
             <version>{version}</version>
             <authors>{authors}</authors>
             <description>Step 20 test fixture package.</description>
           </metadata>
         </package>
         """;

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static IReadOnlyDictionary<string, byte[]> ReadEntries(byte[] nupkgBytes)
    {
        using var stream = new MemoryStream(nupkgBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            result[entry.FullName] = buffer.ToArray();
        }

        return result;
    }

    /// <summary>Returns a copy of <paramref name="nupkgBytes"/> with the given entry
    /// added or replaced.</summary>
    public static byte[] WithEntry(byte[] nupkgBytes, string entryName, byte[] content)
    {
        var entries = new Dictionary<string, byte[]>(ReadEntries(nupkgBytes), StringComparer.Ordinal)
        {
            [entryName] = content
        };
        return Rezip(entries);
    }

    public static byte[] WithCompressedEntry(
        byte[] nupkgBytes,
        string entryName,
        byte[] content)
    {
        var entries = new Dictionary<string, byte[]>(ReadEntries(nupkgBytes), StringComparer.Ordinal)
        {
            [entryName] = content
        };
        return Rezip(entries, CompressionLevel.SmallestSize);
    }

    /// <summary>Returns a copy of <paramref name="nupkgBytes"/> without the given
    /// entry (case-sensitive exact match).</summary>
    public static byte[] WithoutEntry(byte[] nupkgBytes, string entryName)
    {
        var entries = new Dictionary<string, byte[]>(ReadEntries(nupkgBytes), StringComparer.Ordinal);
        entries.Remove(entryName);
        return Rezip(entries);
    }

    private static byte[] Rezip(
        IReadOnlyDictionary<string, byte[]> entries,
        CompressionLevel compressionLevel = CompressionLevel.NoCompression)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                WriteEntry(archive, name, content, compressionLevel);
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        byte[] content,
        CompressionLevel compressionLevel = CompressionLevel.NoCompression)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        using var entryStream = entry.Open();
        entryStream.Write(content, 0, content.Length);
    }

    /// <summary>Packs the real, separately compiled `Contoso.NuExtVault.Flavors`
    /// fixture (`tests/NuExtVault.SdkFixture`) with `dotnet pack`, and returns
    /// everything a test needs to turn it into a complete Step 20 external package
    /// (or a deliberately broken variant of one).</summary>
    public static async Task<ContosoFlavorsAssets> BuildContosoFlavorsAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        const string id = "Contoso.Flavors";
        const string version = "1.2.3";
        const string publisher = "Contoso";
        const string entryAssemblyFileName = "NuExtVault.SdkFixture.dll";
        const string entryType = "NuExtVault.SdkFixture.FlavorsExtension";

        var output = Path.Combine(RepositoryPaths.ArtifactsDirectory, "fixture");
        var result = await RepositoryPaths.DotNetAsync(
            "pack",
            RepositoryPaths.SdkFixtureProjectPath,
            "--configuration",
            "Release",
            "--output",
            Path.GetRelativePath(RepositoryPaths.RepositoryRoot, output));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to pack the Contoso.NuExtVault.Flavors fixture:{Environment.NewLine}{result.Output}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var nupkgPath = Path.Combine(output, $"{id}.{version}.nupkg");
        var entries = ReadEntries(File.ReadAllBytes(nupkgPath));
        var manifestBytes = entries[ManifestEntryName];

        var sdkAssemblyPath = Path.Combine(
            RepositoryPaths.RepositoryRoot,
            "src",
            "NuExtVault.Extensions.Sdk",
            "bin",
            "Release",
            "net10.0",
            "NuExtVault.Extensions.Sdk.dll");
        var dependencyAssemblyPath = Path.Combine(
            RepositoryPaths.RepositoryRoot,
            "tests",
            "NuExtVault.SdkFixture.Dependency",
            "bin",
            "Release",
            "net10.0",
            "Contoso.Flavors.Dependency.dll");

        return new ContosoFlavorsAssets(
            id,
            version,
            publisher,
            entryAssemblyFileName,
            entryType,
            manifestBytes,
            entries[$"{LibDirectory}{entryAssemblyFileName}"],
            File.Exists(sdkAssemblyPath) ? File.ReadAllBytes(sdkAssemblyPath) : null,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Contoso.Flavors.Dependency.dll"] =
                    File.ReadAllBytes(dependencyAssemblyPath)
            });
    }

    /// <summary>Builds the default, fully valid Step 20 package for
    /// <paramref name="assets"/>: manifest + package.json + attestation signed by
    /// <paramref name="trustedKey"/> + the entry assembly.</summary>
    public static byte[] BuildValidPackage(
        ContosoFlavorsAssets assets,
        ECDsa trustedKey,
        string keyId = ConformanceAttestationFixture.DefaultKeyId,
        ExternalExtensionDependencySpec[]? extensionDependencies = null,
        bool bundlePrivateSdkCopy = false)
    {
        var packageJson = ExternalExtensionPackageJson.Build(
            assets.EntryAssemblyFileName,
            assets.EntryType,
            extensionDependencies ?? []);
        var payload = ConformanceAttestationFixture.BuildPayload(
            assets.Id,
            assets.Version,
            assets.Publisher,
            Sha256Hex(assets.ManifestJsonBytes),
            StructuralSha256());
        var attestation = ConformanceAttestationFixture.SignToAttestationJson(
            payload,
            trustedKey,
            keyId);

        var libFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [assets.EntryAssemblyFileName] = assets.EntryAssemblyBytes
        };
        foreach (var (fileName, bytes) in assets.PrivateAssemblyBytes)
        {
            libFiles[fileName] = bytes;
        }
        if (bundlePrivateSdkCopy && assets.SdkAssemblyBytes is not null)
        {
            libFiles["NuExtVault.Extensions.Sdk.dll"] = assets.SdkAssemblyBytes;
        }

        return BuildNupkg(
            assets.Id,
            assets.Version,
            assets.ManifestJsonBytes,
            packageJson,
            attestation,
            libFiles);
    }

    /// <summary>Builds a signed Step 20 package for <paramref name="assets"/>'s real
    /// entry assembly, but with an arbitrary (possibly malicious) manifest, so
    /// manifest-level negative tests (squatting, dependency graph, traversal via a
    /// crafted id, etc.) do not need their own compiled assembly.</summary>
    public static byte[] BuildPackageWithManifest(
        ContosoFlavorsAssets assets,
        byte[] manifestJsonBytes,
        ECDsa trustedKey,
        string keyId = ConformanceAttestationFixture.DefaultKeyId,
        ExternalExtensionDependencySpec[]? extensionDependencies = null,
        string? publisherOverride = null)
    {
        var packageJson = ExternalExtensionPackageJson.Build(
            assets.EntryAssemblyFileName,
            assets.EntryType,
            extensionDependencies ?? []);
        var payload = ConformanceAttestationFixture.BuildPayload(
            assets.Id,
            assets.Version,
            publisherOverride ?? assets.Publisher,
            Sha256Hex(manifestJsonBytes),
            StructuralSha256());
        var attestation = ConformanceAttestationFixture.SignToAttestationJson(
            payload,
            trustedKey,
            keyId);

        return BuildNupkg(
            assets.Id,
            assets.Version,
            manifestJsonBytes,
            packageJson,
            attestation,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [assets.EntryAssemblyFileName] = assets.EntryAssemblyBytes
            });
    }

    /// <summary>Builds a signed Step 20 package that reuses the real Contoso Flavors
    /// entry assembly bytes under a distinct declared package identity. This lets
    /// multi-package tests (dependency graph, duplicate/case-collision, count
    /// limits) build several genuinely loadable packages without a second
    /// `dotnet pack` invocation.</summary>
    public static byte[] BuildCompanionPackage(
        ContosoFlavorsAssets assets,
        string id,
        string version,
        ECDsa trustedKey,
        string keyId = ConformanceAttestationFixture.DefaultKeyId,
        ExternalExtensionDependencySpec[]? extensionDependencies = null,
        string publisher = ConformanceAttestationFixture.DefaultPublisher,
        string? requiredCapability = null,
        string? routeId = null,
        string? routePath = null,
        string? operationId = null,
        string? resourceId = null)
    {
        var manifestJsonBytes = MinimalManifestJson.Build(
            id,
            version,
            publisher,
            requiredCapability,
            routeId,
            routePath,
            operationId,
            resourceId);
        var packageJson = ExternalExtensionPackageJson.Build(
            assets.EntryAssemblyFileName,
            assets.EntryType,
            extensionDependencies ?? []);
        var payload = ConformanceAttestationFixture.BuildPayload(
            id,
            version,
            publisher,
            Sha256Hex(manifestJsonBytes),
            StructuralSha256());
        var attestation = ConformanceAttestationFixture.SignToAttestationJson(
            payload,
            trustedKey,
            keyId);

        return BuildNupkg(
            id,
            version,
            manifestJsonBytes,
            packageJson,
            attestation,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [assets.EntryAssemblyFileName] = assets.EntryAssemblyBytes
            });
    }

    public static string StructuralSha256() => File.ReadAllText(
        Path.Combine(
            RepositoryPaths.RepositoryRoot,
            "tests",
            "NuExtVault.Extensions.Sdk.Tests",
            "Snapshots",
            "sdk-v1.structural-contract.sha256")).Trim();
}

public sealed record ContosoFlavorsAssets(
    string Id,
    string Version,
    string Publisher,
    string EntryAssemblyFileName,
    string EntryType,
    byte[] ManifestJsonBytes,
    byte[] EntryAssemblyBytes,
    byte[]? SdkAssemblyBytes,
    IReadOnlyDictionary<string, byte[]> PrivateAssemblyBytes);
