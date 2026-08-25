using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Hosting;

internal sealed record ExternalExtensionLimits(
    int MaximumPackageCount = 32,
    long MaximumPackageBytes = 16 * 1024 * 1024,
    long MaximumTotalBytes = 128 * 1024 * 1024,
    int MaximumEntryCount = 4096,
    long MaximumEntryBytes = 8 * 1024 * 1024)
{
    public static ExternalExtensionLimits Default { get; } = new();
}

internal sealed record ExternalExtensionConfiguration(
    ImmutableArray<string> Roots,
    ImmutableArray<ConformanceTrustRoot> TrustRoots,
    TimeProvider TimeProvider,
    ExternalExtensionLimits? Limits = null)
{
    public static ExternalExtensionConfiguration Disabled { get; } =
        new([], [], TimeProvider.System, ExternalExtensionLimits.Default);

    public bool IsEnabled => !Roots.IsDefaultOrEmpty;
}

internal sealed record ExternalExtensionLoadResult(
    string PackageId,
    string Version,
    bool Succeeded,
    string? FailureCode,
    string? RedactedMessage,
    ValidatedExtensionActivationIdentity? ActivationIdentity = null);

internal sealed record ValidatedExtensionActivationIdentity(
    string PackageId,
    string PackageVersion,
    string ManifestId,
    string ManifestVersion,
    string ModuleAssemblyIdentity,
    string Publisher,
    string PublisherKeyId,
    string ManifestDigest,
    string ClosureDigest,
    ContractVersionSet SelectedContracts,
    string StagedContentIdentity);

internal sealed record ExternalExtensionDiagnostics(
    ImmutableArray<ExternalExtensionLoadResult> Results)
{
    public static ExternalExtensionDiagnostics Empty { get; } = new([]);
}

internal sealed record ExternalExtensionStagedPackage(
    string PackageId,
    string Version,
    string StageDirectory,
    string EntryAssemblyPath);

internal sealed record ExternalExtensionLoadTestHooks(
    Action<ExternalExtensionStagedPackage>? AfterValidation = null);

internal sealed class ExternalExtensionRuntime : IDisposable, IHostedService
{
    private ImmutableArray<PackageLoadContext> _contexts;

    internal ExternalExtensionRuntime(
        ImmutableArray<IExtensionModule> modules,
        ExternalExtensionDiagnostics diagnostics,
        ImmutableArray<PackageLoadContext> contexts)
    {
        Modules = modules;
        Diagnostics = diagnostics;
        _contexts = contexts;
    }

    public ImmutableArray<IExtensionModule> Modules { get; private set; }

    public ExternalExtensionDiagnostics Diagnostics { get; }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Modules = [];
        foreach (var context in _contexts.Reverse())
        {
            context.UnloadAndDelete();
        }
        _contexts = [];
    }
}

internal static class ExternalExtensionPackageLoader
{
    private const string PackageMetadataName = "extension-package.json";
    private const string ManifestName = "extension-manifest.json";
    private const string AttestationName = "extension-attestation.json";
    private static readonly string SdkName = typeof(IExtensionModule).Assembly.GetName().Name!;
    private static readonly ImmutableHashSet<string> ForbiddenAssemblies =
    [
        "NuExtVault",
        "NuExtVault.Kernel",
        "NuExtVault.Extensions.Official"
    ];

    public static ExternalExtensionRuntime Load(
        ExternalExtensionConfiguration configuration,
        ExternalExtensionLoadTestHooks? testHooks = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsEnabled)
        {
            return new ExternalExtensionRuntime([], ExternalExtensionDiagnostics.Empty, []);
        }

        var limits = configuration.Limits ?? ExternalExtensionLimits.Default;
        ValidateLimits(limits);
        var packagePaths = Discover(configuration.Roots, limits);
        if (packagePaths.Length > limits.MaximumPackageCount)
        {
            return new ExternalExtensionRuntime(
                [],
                new ExternalExtensionDiagnostics(
                [new ExternalExtensionLoadResult(
                    "unknown",
                    "unknown",
                    false,
                    "external-extension.too-many-packages",
                    "Configured extension roots contain too many packages.")]),
                []);
        }
        var staged = new List<StagedPackage>();
        var failures = new List<ExternalExtensionLoadResult>();
        long retainedAssemblyBytes = 0;
        try
        {
            foreach (var packagePath in packagePaths)
            {
                try
                {
                    var package = Stage(
                        packagePath,
                        configuration,
                        limits,
                        limits.MaximumTotalBytes - retainedAssemblyBytes);
                    retainedAssemblyBytes = checked(retainedAssemblyBytes + package.RetainedAssemblyBytes);
                    if (retainedAssemblyBytes > limits.MaximumTotalBytes)
                    {
                        package.Dispose();
                        throw new ExternalExtensionException(
                            package.Id,
                            package.Version,
                            "external-extension.memory-limit-exceeded",
                            "Validated extension assembly bytes exceed the configured total size limit.");
                    }
                    staged.Add(package);
                }
                catch (ExternalExtensionException exception)
                {
                    failures.Add(new ExternalExtensionLoadResult(
                        exception.PackageId,
                        exception.Version,
                        false,
                        exception.Code,
                        exception.Message));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException or CryptographicException or
                    OverflowException)
                {
                    failures.Add(new ExternalExtensionLoadResult(
                        "unknown",
                        "unknown",
                        false,
                        "external-extension.package-invalid",
                        $"A discovered extension package is invalid: {exception.GetBaseException().Message}"));
                }
            }

            ImmutableArray<StagedPackage> ordered;
            try
            {
                ValidateUniqueIdentities(staged);
                ordered = OrderDependencies(staged);
            }
            catch (ExternalExtensionException exception)
            {
                failures.Add(new ExternalExtensionLoadResult(
                    exception.PackageId,
                    exception.Version,
                    false,
                    exception.Code,
                    exception.Message));
                return Failed(staged, failures);
            }
            if (failures.Count > 0)
            {
                return Failed(staged, failures);
            }

            if (testHooks?.AfterValidation is { } afterValidation)
            {
                foreach (var package in ordered)
                {
                    afterValidation(new ExternalExtensionStagedPackage(
                        package.Id,
                        package.Version,
                        package.StageDirectory,
                        package.EntryAssemblyPath));
                }
            }

            var modules = ImmutableArray.CreateBuilder<IExtensionModule>();
            var contexts = ImmutableArray.CreateBuilder<PackageLoadContext>();
            var results = ImmutableArray.CreateBuilder<ExternalExtensionLoadResult>();
            foreach (var package in ordered)
            {
                PackageLoadContext? context = null;
                try
                {
                    context = new PackageLoadContext(
                        package.StageDirectory,
                        package.Assemblies);
                    var assembly = context.LoadEntryAssembly(package.EntryAssembly);
                    if (!AssemblyName.ReferenceMatchesDefinition(
                            assembly.GetName(),
                            package.EntryAssembly.Identity))
                    {
                        throw Failure(
                            package,
                            "external-extension.module-identity-mismatch",
                            "The activated module assembly identity does not match the validated identity.");
                    }
                    var type = assembly.GetType(package.Metadata.EntryType, throwOnError: true)
                        ?? throw new TypeLoadException(package.Metadata.EntryType);
                    if (!typeof(IExtensionModule).IsAssignableFrom(type))
                    {
                        throw Failure(
                            package,
                            "external-extension.sdk-type-identity",
                            "The configured entry type does not implement the shared SDK module contract.");
                    }

                    var module = (IExtensionModule)(Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException("The extension module could not be created."));
                    if (!module.Contribution.Manifest.Equals(package.Manifest))
                    {
                        throw Failure(
                            package,
                            "external-extension.module-manifest-mismatch",
                            "The module contribution does not exactly match the validated package manifest.");
                    }

                    var materialized = PublicExtensionModuleAdapter.Materialize(
                        module,
                        package.Identity.ManifestDigest,
                        package.Identity.StagedContentIdentity);
                    contexts.Add(context);
                    modules.Add(materialized);
                    results.Add(new ExternalExtensionLoadResult(
                        package.Id,
                        package.Version,
                        true,
                        null,
                        $"Loaded trusted package '{package.Id}' version '{package.Version}' " +
                        $"with manifest digest '{package.Identity.ManifestDigest}', closure digest " +
                        $"'{package.Identity.ClosureDigest}', and staged content identity " +
                        $"'{package.Identity.StagedContentIdentity}'.",
                        package.Identity));
                }
                catch (ExternalExtensionException exception)
                {
                    context?.UnloadAndDelete();
                    failures.Add(new ExternalExtensionLoadResult(
                        exception.PackageId,
                        exception.Version,
                        false,
                        exception.Code,
                        exception.Message));
                    break;
                }
                catch (Exception exception) when (
                    exception is FileLoadException or FileNotFoundException or
                    BadImageFormatException or TypeLoadException or
                    MissingMethodException or TargetInvocationException or
                    InvalidOperationException)
                {
                    context?.UnloadAndDelete();
                    failures.Add(new ExternalExtensionLoadResult(
                        package.Id,
                        package.Version,
                        false,
                        "external-extension.activation-failed",
                        $"Package '{package.Id}' activation failed: {exception.GetBaseException().Message}"));
                    break;
                }
            }

            if (failures.Count > 0)
            {
                foreach (var context in contexts.ToImmutable().Reverse())
                {
                    context.UnloadAndDelete();
                }
                return Failed(staged, failures);
            }

            return new ExternalExtensionRuntime(
                modules.ToImmutable(),
                new ExternalExtensionDiagnostics(
                    [.. results.OrderBy(result => result.PackageId, StringComparer.OrdinalIgnoreCase)]),
                contexts.ToImmutable());
        }
        finally
        {
            foreach (var package in staged)
            {
                package.DisposeStageOnFailure = false;
            }
        }
    }

    private static ExternalExtensionRuntime Failed(
        IEnumerable<StagedPackage> staged,
        IEnumerable<ExternalExtensionLoadResult> failures)
    {
        foreach (var package in staged)
        {
            package.Dispose();
        }

        return new ExternalExtensionRuntime(
            [],
            new ExternalExtensionDiagnostics(
                [.. failures.OrderBy(result => result.PackageId, StringComparer.OrdinalIgnoreCase)]),
            []);
    }

    private static ImmutableArray<string> Discover(
        ImmutableArray<string> roots,
        ExternalExtensionLimits limits)
    {
        if (roots.IsDefaultOrEmpty)
        {
            return [];
        }

        var canonicalRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<string>();
        long totalBytes = 0;
        foreach (var configuredRoot in roots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot) ||
                configuredRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new ServerHostingConfigurationException(
                    "external-extension.root-invalid: An extension root is malformed.");
            }

            var root = Path.GetFullPath(configuredRoot);
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            {
                throw new ServerHostingConfigurationException(
                    "external-extension.root-invalid: A configured extension root does not exist.");
            }
            if (!canonicalRoots.Add(root))
            {
                throw new ServerHostingConfigurationException(
                    "external-extension.root-duplicate: An extension root is configured more than once.");
            }
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ServerHostingConfigurationException(
                    "external-extension.root-reparse-point: Extension roots cannot be symbolic links.");
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.nupkg", SearchOption.TopDirectoryOnly))
            {
                var fullPath = Path.GetFullPath(path);
                if (!IsUnder(root, fullPath) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ServerHostingConfigurationException(
                        "external-extension.package-path-invalid: A discovered package escaped its root.");
                }

                var length = new FileInfo(fullPath).Length;
                if (length > limits.MaximumPackageBytes)
                {
                    packages.Add(fullPath);
                    continue;
                }
                totalBytes = checked(totalBytes + length);
                packages.Add(fullPath);
            }
        }

        if (packages.Count > limits.MaximumPackageCount)
        {
            return [.. packages.Order(StringComparer.OrdinalIgnoreCase).Take(limits.MaximumPackageCount + 1)];
        }
        if (totalBytes > limits.MaximumTotalBytes)
        {
            throw new ServerHostingConfigurationException(
                "external-extension.total-size-exceeded: Configured extension packages exceed the total size limit.");
        }

        return [.. packages.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static StagedPackage Stage(
        string packagePath,
        ExternalExtensionConfiguration configuration,
        ExternalExtensionLimits limits,
        long remainingAssemblyBytes)
    {
        var packageLength = new FileInfo(packagePath).Length;
        if (packageLength > limits.MaximumPackageBytes)
        {
            throw new ExternalExtensionException(
                "unknown",
                "unknown",
                "external-extension.package-too-large",
                "A discovered package exceeds the configured package size limit.");
        }

        var stage = Path.Combine(Path.GetTempPath(), $"nuextvault-extension-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            using var stream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > limits.MaximumEntryCount)
            {
                throw new ExternalExtensionException(
                    "unknown", "unknown", "external-extension.too-many-entries",
                    "A discovered package exceeds the configured entry-count limit.");
            }

            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.Length == 0 || normalized.StartsWith('/') ||
                    normalized.Split('/').Any(segment => segment is "" or "." or "..") ||
                    !entryNames.Add(normalized))
                {
                    throw new ExternalExtensionException(
                        "unknown", "unknown", "external-extension.path-traversal",
                        "A package contains an unsafe or case-colliding entry path.");
                }
                if (entry.Length > limits.MaximumEntryBytes)
                {
                    throw new ExternalExtensionException(
                        "unknown", "unknown", "external-extension.entry-too-large",
                        "A package entry exceeds the configured size limit.");
                }
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > limits.MaximumTotalBytes)
                {
                    throw new ExternalExtensionException(
                        "unknown", "unknown", "external-extension.expanded-size-exceeded",
                        "A package exceeds the configured expanded-size limit.");
                }

                var destination = Path.GetFullPath(Path.Combine(stage, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsUnder(stage, destination))
                {
                    throw new ExternalExtensionException(
                        "unknown", "unknown", "external-extension.path-traversal",
                        "A package entry escapes its staging directory.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(input, output, entry.Length, limits.MaximumEntryBytes);
            }

            var nuspecPath = Directory.EnumerateFiles(stage, "*.nuspec", SearchOption.TopDirectoryOnly)
                .SingleOrDefault()
                ?? throw new InvalidDataException("The package must contain one root nuspec.");
            var (packageId, packageVersion) = ReadNuspec(nuspecPath);
            var manifestBytes = File.ReadAllBytes(RequiredRootFile(stage, ManifestName));
            ExtensionManifest manifest;
            try
            {
                manifest = ExtensionManifestJson.Parse(manifestBytes);
            }
            catch (FormatException exception)
            {
                var code = exception.Message.Contains(
                    "operation.identity.not-owned",
                    StringComparison.Ordinal)
                    ? "external-extension.operation-squatting"
                    : exception.Message.Contains(
                        "contribution.identity.not-owned",
                        StringComparison.Ordinal)
                        ? "external-extension.resource-squatting"
                        : "external-extension.manifest-invalid";
                throw new ExternalExtensionException(
                    packageId,
                    packageVersion,
                    code,
                    $"Package '{packageId}' has an invalid manifest.");
            }
            // Extension package identity is deliberately stricter than NuGet lookup
            // identity: no case folding, trimming, or version normalization is allowed.
            if (!string.Equals(packageId, manifest.Identity.Id, StringComparison.Ordinal) ||
                !string.Equals(packageVersion, manifest.Identity.Version, StringComparison.Ordinal))
            {
                throw new ExternalExtensionException(
                    packageId, packageVersion, "external-extension.package-identity-mismatch",
                    $"Package '{packageId}' identity does not exactly match its manifest.");
            }

            var metadata = ReadPackageMetadata(RequiredRootFile(stage, PackageMetadataName));
            ValidateManifest(manifest);
            var publisherKeyId = ValidateAttestation(
                RequiredRootFile(stage, AttestationName),
                packageId,
                packageVersion,
                manifest,
                manifestBytes,
                configuration);

            var libRoot = Path.Combine(stage, "lib", "net10.0");
            var entryAssemblyPath = Path.GetFullPath(Path.Combine(libRoot, metadata.EntryAssembly));
            if (!IsUnder(libRoot, entryAssemblyPath) ||
                !File.Exists(entryAssemblyPath) ||
                !string.Equals(Path.GetFileName(entryAssemblyPath), metadata.EntryAssembly, StringComparison.Ordinal))
            {
                throw new ExternalExtensionException(
                    packageId, packageVersion, "external-extension.entry-assembly-invalid",
                    $"Package '{packageId}' has an invalid entry assembly.");
            }
            if (File.Exists(Path.Combine(libRoot, SdkName + ".dll")))
            {
                throw new ExternalExtensionException(
                    packageId, packageVersion, "external-extension.duplicate-sdk-identity",
                    $"Package '{packageId}' bundles the host SDK assembly.");
            }

            var assemblies = ImmutableDictionary.CreateBuilder<string, VerifiedAssemblyImage>(
                StringComparer.OrdinalIgnoreCase);
            long retainedBytes = 0;
            foreach (var assemblyPath in Directory.EnumerateFiles(
                         libRoot, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
                var assemblyLength = new FileInfo(assemblyPath).Length;
                var symbolsLength = File.Exists(symbolsPath) ? new FileInfo(symbolsPath).Length : 0;
                retainedBytes = checked(retainedBytes + assemblyLength + symbolsLength);
                if (retainedBytes > remainingAssemblyBytes)
                {
                    throw new ExternalExtensionException(
                        packageId,
                        packageVersion,
                        "external-extension.memory-limit-exceeded",
                        "Validated extension assembly bytes exceed the configured total size limit.");
                }
                var assemblyBytes = File.ReadAllBytes(assemblyPath);
                var assemblyName = ReadAndValidateAssembly(
                    assemblyBytes,
                    packageId,
                    packageVersion,
                    requireSdkReference: string.Equals(
                        Path.GetFileName(assemblyPath),
                        metadata.EntryAssembly,
                        StringComparison.Ordinal));
                if (ForbiddenAssemblies.Contains(assemblyName.Name ?? string.Empty) ||
                    PackageLoadContext.IsPlatformAssembly(assemblyName.Name))
                {
                    throw new ExternalExtensionException(
                        packageId,
                        packageVersion,
                        "external-extension.duplicate-host-assembly",
                        $"Package '{packageId}' bundles a host or framework assembly identity.");
                }
                if (string.Equals(assemblyName.Name, SdkName, StringComparison.Ordinal))
                {
                    throw new ExternalExtensionException(
                        packageId, packageVersion, "external-extension.duplicate-sdk-identity",
                        $"Package '{packageId}' bundles the host SDK assembly.");
                }

                var symbolsBytes = File.Exists(symbolsPath) ? File.ReadAllBytes(symbolsPath) : null;
                var image = new VerifiedAssemblyImage(
                    Path.GetFileName(assemblyPath),
                    assemblyName,
                    assemblyBytes,
                    symbolsBytes);
                if (!assemblies.TryAdd(assemblyName.Name!, image))
                {
                    throw new ExternalExtensionException(
                        packageId,
                        packageVersion,
                        "external-extension.duplicate-assembly-identity",
                        $"Package '{packageId}' contains duplicate private assembly identities.");
                }
            }
            var entryAssembly = assemblies.Values.SingleOrDefault(image =>
                string.Equals(image.FileName, metadata.EntryAssembly, StringComparison.Ordinal))
                ?? throw new ExternalExtensionException(
                    packageId, packageVersion, "external-extension.entry-assembly-invalid",
                    $"Package '{packageId}' has an invalid entry assembly.");
            var manifestDigest = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            var stageDigest = ComputeStageDigest(stage);
            var closureDigest = ComputeClosureDigest(assemblies.Values);
            var identity = new ValidatedExtensionActivationIdentity(
                packageId,
                packageVersion,
                manifest.Identity.Id,
                manifest.Identity.Version,
                entryAssembly.Identity.FullName!,
                manifest.Identity.Publisher,
                publisherKeyId,
                manifestDigest,
                closureDigest,
                manifest.Contracts,
                stageDigest);
            return new StagedPackage(
                packageId,
                packageVersion,
                manifest,
                metadata,
                stage,
                entryAssemblyPath,
                entryAssembly,
                assemblies.ToImmutable(),
                identity);
        }
        catch
        {
            Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static void ValidateManifest(ExtensionManifest manifest)
    {
        if (manifest.Identity.Id.Equals("NuGet", StringComparison.OrdinalIgnoreCase) ||
            manifest.Identity.Id.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase) ||
            manifest.Identity.Id.Equals("NuExtVault", StringComparison.OrdinalIgnoreCase) ||
            manifest.Identity.Id.Equals("builtin", StringComparison.OrdinalIgnoreCase) ||
            manifest.Identity.Id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalExtensionException(
                manifest.Identity.Id,
                manifest.Identity.Version,
                "external-extension.identity-squatting",
                $"Package '{manifest.Identity.Id}' claims a reserved extension identity.");
        }

        var conformance = manifest.Operations
            .Select(operation => ExtensionConformance.ValidateOwnership(manifest.Identity.Id, operation))
            .FirstOrDefault(result => !result.IsValid);
        if (conformance is not null)
        {
            throw new ExternalExtensionException(
                manifest.Identity.Id,
                manifest.Identity.Version,
                "external-extension.operation-squatting",
                $"Package '{manifest.Identity.Id}' claims an operation outside its namespace.");
        }

        foreach (var route in manifest.Routes)
        {
            if (route.Path.StartsWith("/v3/", StringComparison.OrdinalIgnoreCase) ||
                route.Path.StartsWith("/package", StringComparison.OrdinalIgnoreCase) ||
                route.Path.StartsWith("/__test", StringComparison.OrdinalIgnoreCase) ||
                route.Path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExternalExtensionException(
                    manifest.Identity.Id,
                    manifest.Identity.Version,
                    "external-extension.route-squatting",
                    $"Package '{manifest.Identity.Id}' claims a reserved route.");
            }
        }

        foreach (var contribution in manifest.Contributions)
        {
            // Reserved prefixes stop one extension from claiming another owner's
            // contribution identity. An identity inside the declaring extension's own
            // namespace is never squatting, even when the extension itself is published
            // under a reserved prefix.
            var owned = contribution.Identity.Value.StartsWith(
                manifest.Identity.Id + ".",
                StringComparison.Ordinal);
            if (!owned &&
                (contribution.Identity.Value.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase) ||
                 contribution.Identity.Value.StartsWith("NuExtVault.", StringComparison.OrdinalIgnoreCase) ||
                 contribution.Identity.Value.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExternalExtensionException(
                    manifest.Identity.Id,
                    manifest.Identity.Version,
                    "external-extension.resource-squatting",
                    $"Package '{manifest.Identity.Id}' claims a reserved contribution.");
            }
        }
    }

    private static string ValidateAttestation(
        string path,
        string packageId,
        string packageVersion,
        ExtensionManifest manifest,
        byte[] manifestBytes,
        ExternalExtensionConfiguration configuration)
    {
        AttestationFile file;
        try
        {
            file = JsonSerializer.Deserialize<AttestationFile>(
                File.ReadAllBytes(path),
                JsonOptions()) ?? throw new JsonException("Empty attestation.");
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException)
        {
            throw new ExternalExtensionException(
                packageId, packageVersion, "external-extension.attestation-invalid",
                $"Package '{packageId}' has an invalid attestation.");
        }

        var envelope = new ConformanceAttestationEnvelope(
            Convert.FromBase64String(file.PayloadBase64),
            Convert.FromBase64String(file.SignatureBase64),
            file.Algorithm,
            file.KeyId,
            file.IssuedAt,
            file.ExpiresAt);
        var structural = StructuralContractFingerprint.Create(typeof(IExtensionModule).Assembly);
        var expectation = new ConformanceExpectation(
            packageId,
            packageVersion,
            manifest.Identity.Publisher,
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            ExtensionSdkVersions.Current,
            manifest.Contracts.Manifest,
            manifest.Contracts.Operation,
            manifest.Contracts.Contribution,
            manifest.Contracts.Route,
            manifest.Contracts.Capability,
            manifest.Contracts.Structural,
            structural.Sha256,
            ExtensionSdkVersions.ConformanceSuiteV1);
        var verification = ConformanceAttestationVerifier.Verify(
            envelope,
            expectation,
            configuration.TrustRoots.IsDefault ? [] : configuration.TrustRoots,
            configuration.TimeProvider.GetUtcNow());
        if (!verification.IsValid)
        {
            var code = verification.Failure switch
            {
                AttestationFailure.TrustRootMissing => "external-extension.trust-root-missing",
                AttestationFailure.Expired => "external-extension.attestation-expired",
                _ => "external-extension.attestation-invalid"
            };
            throw new ExternalExtensionException(
                packageId,
                packageVersion,
                code,
                $"Package '{packageId}' failed attestation verification ({verification.Failure}).");
        }
        return envelope.KeyId;
    }

    private static PackageMetadata ReadPackageMetadata(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            var root = document.RootElement;
            var allowed = new HashSet<string>(
                ["$schema", "schemaVersion", "entryAssembly", "entryType", "dependencies"],
                StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
                root.GetProperty("$schema").GetString() !=
                    "https://schemas.nuextvault.dev/extensions/package/v1" ||
                root.GetProperty("schemaVersion").GetInt32() != 1)
            {
                throw new JsonException("Unsupported package metadata.");
            }

            var dependencies = root.GetProperty("dependencies").EnumerateArray()
                .Select(value => new PackageDependency(
                    value.GetProperty("id").GetString()!,
                    ParseVersion(value.GetProperty("minimumInclusive").GetString()!),
                    ParseVersion(value.GetProperty("maximumExclusive").GetString()!)))
                .OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            return new PackageMetadata(
                root.GetProperty("entryAssembly").GetString()
                    ?? throw new JsonException("Missing entry assembly."),
                root.GetProperty("entryType").GetString()
                    ?? throw new JsonException("Missing entry type."),
                dependencies);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new ExternalExtensionException(
                "unknown", "unknown", "external-extension.package-metadata-invalid",
                "A package has invalid loading metadata.");
        }
    }

    private static ImmutableArray<StagedPackage> OrderDependencies(List<StagedPackage> packages)
    {
        var byId = packages.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = ImmutableArray.CreateBuilder<StagedPackage>();
        foreach (var package in packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase))
        {
            Visit(package);
        }
        return ordered.ToImmutable();

        void Visit(StagedPackage package)
        {
            if (state.TryGetValue(package.Id, out var current))
            {
                if (current == 2) return;
                throw Failure(
                    package,
                    "external-extension.dependency-cycle",
                    $"Package '{package.Id}' is part of an extension dependency cycle.");
            }
            state[package.Id] = 1;
            foreach (var dependency in package.Metadata.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Id, out var provider))
                {
                    throw Failure(
                        package,
                        "external-extension.dependency-missing",
                        $"Package '{package.Id}' requires a missing extension.");
                }
                var version = ParseVersion(provider.Version);
                if (Compare(version, dependency.Minimum) < 0 ||
                    Compare(version, dependency.MaximumExclusive) >= 0)
                {
                    throw Failure(
                        package,
                        "external-extension.dependency-range-unsatisfied",
                        $"Package '{package.Id}' has an unsatisfied extension dependency.");
                }
                Visit(provider);
            }
            state[package.Id] = 2;
            ordered.Add(package);
        }
    }

    private static void ValidateUniqueIdentities(List<StagedPackage> packages)
    {
        var duplicate = packages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            var first = duplicate.First();
            throw Failure(
                first,
                "external-extension.duplicate-identity",
                $"Extension identity '{first.Id}' is installed more than once.");
        }
    }

    private static AssemblyName ReadAndValidateAssembly(
        byte[] assemblyBytes,
        string packageId,
        string packageVersion,
        bool requireSdkReference)
    {
        using var stream = new MemoryStream(assemblyBytes, writable: false);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
        {
            throw new ExternalExtensionException(
                packageId, packageVersion, "external-extension.entry-assembly-invalid",
                $"Package '{packageId}' entry assembly has no CLR metadata.");
        }

        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetAssemblyReference(handle))
            .Select(reference => (
                Name: metadata.GetString(reference.Name),
                Version: reference.Version))
            .ToArray();
        var forbidden = references.FirstOrDefault(reference =>
            ForbiddenAssemblies.Contains(reference.Name) ||
            (reference.Name.StartsWith("NuExtVault.", StringComparison.Ordinal) &&
             !string.Equals(reference.Name, SdkName, StringComparison.Ordinal)));
        if (!string.IsNullOrEmpty(forbidden.Name))
        {
            throw new ExternalExtensionException(
                packageId,
                packageVersion,
                "external-extension.forbidden-reference",
                $"Package '{packageId}' references a forbidden host assembly.");
        }

        var sdkReference = references.SingleOrDefault(reference =>
            string.Equals(reference.Name, SdkName, StringComparison.Ordinal));
        var hostSdk = typeof(IExtensionModule).Assembly.GetName();
        if (requireSdkReference &&
            (string.IsNullOrEmpty(sdkReference.Name) || sdkReference.Version != hostSdk.Version))
        {
            throw new ExternalExtensionException(
                packageId,
                packageVersion,
                "external-extension.sdk-identity-mismatch",
                $"Package '{packageId}' does not reference the exact host SDK identity.");
        }

        var definition = metadata.GetAssemblyDefinition();
        var identity = new AssemblyName
        {
            Name = metadata.GetString(definition.Name),
            Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : metadata.GetString(definition.Culture)
        };
        if (!definition.PublicKey.IsNil)
        {
            identity.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
        }
        return identity;
    }

    private static (string Id, string Version) ReadNuspec(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        XNamespace ns = document.Root?.Name.Namespace
            ?? throw new InvalidDataException("Invalid nuspec.");
        var metadata = document.Root?.Element(ns + "metadata")
            ?? throw new InvalidDataException("Missing nuspec metadata.");
        return (
            metadata.Element(ns + "id")?.Value
                ?? throw new InvalidDataException("Missing package ID."),
            metadata.Element(ns + "version")?.Value
                ?? throw new InvalidDataException("Missing package version."));
    }

    private static string RequiredRootFile(string stage, string name)
    {
        var path = Path.Combine(stage, name);
        return File.Exists(path)
            ? path
            : throw new InvalidDataException($"Package is missing '{name}'.");
    }

    private static string ComputeStageDigest(string stage)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
                     .OrderBy(file => Path.GetRelativePath(stage, file), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ComputeClosureDigest(IEnumerable<VerifiedAssemblyImage> assemblies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var assembly in assemblies.OrderBy(image => image.FileName, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(assembly.FileName));
            hash.AppendData([0]);
            hash.AppendData(assembly.AssemblyBytes);
            if (assembly.SymbolsBytes is { } symbols)
            {
                hash.AppendData([0]);
                hash.AppendData(symbols);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static void CopyBounded(Stream input, Stream output, long declared, long maximum)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            copied = checked(copied + read);
            if (copied > maximum || copied > declared)
            {
                throw new InvalidDataException("A package entry exceeded its declared size.");
            }
            output.Write(buffer, 0, read);
        }
        if (copied != declared)
        {
            throw new InvalidDataException("A package entry did not match its declared size.");
        }
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version) && value.Count(character => character == '.') == 2
            ? version
            : throw new FormatException("Versions must use major.minor.patch.");

    private static int Compare(Version left, Version right) => left.CompareTo(right);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static void ValidateLimits(ExternalExtensionLimits limits)
    {
        if (limits.MaximumPackageCount <= 0 ||
            limits.MaximumPackageBytes <= 0 ||
            limits.MaximumTotalBytes <= 0 ||
            limits.MaximumEntryCount <= 0 ||
            limits.MaximumEntryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    private static ExternalExtensionException Failure(
        StagedPackage package,
        string code,
        string message) =>
        new(package.Id, package.Version, code, message);

    private sealed record AttestationFile(
        string PayloadBase64,
        string SignatureBase64,
        string Algorithm,
        string KeyId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);

    private sealed record PackageMetadata(
        string EntryAssembly,
        string EntryType,
        ImmutableArray<PackageDependency> Dependencies);

    private sealed record PackageDependency(
        string Id,
        Version Minimum,
        Version MaximumExclusive);

    internal sealed record VerifiedAssemblyImage(
        string FileName,
        AssemblyName Identity,
        byte[] AssemblyBytes,
        byte[]? SymbolsBytes)
    {
        public long RetainedBytes => AssemblyBytes.LongLength + (SymbolsBytes?.LongLength ?? 0);
    }

    private sealed class StagedPackage(
        string id,
        string version,
        ExtensionManifest manifest,
        PackageMetadata metadata,
        string stageDirectory,
        string entryAssemblyPath,
        VerifiedAssemblyImage entryAssembly,
        ImmutableDictionary<string, VerifiedAssemblyImage> assemblies,
        ValidatedExtensionActivationIdentity identity) : IDisposable
    {
        public string Id { get; } = id;
        public string Version { get; } = version;
        public ExtensionManifest Manifest { get; } = manifest;
        public PackageMetadata Metadata { get; } = metadata;
        public string StageDirectory { get; } = stageDirectory;
        public string EntryAssemblyPath { get; } = entryAssemblyPath;
        public VerifiedAssemblyImage EntryAssembly { get; } = entryAssembly;
        public ImmutableDictionary<string, VerifiedAssemblyImage> Assemblies { get; } = assemblies;
        public ValidatedExtensionActivationIdentity Identity { get; } = identity;
        public long RetainedAssemblyBytes { get; } = assemblies.Values.Sum(image => image.RetainedBytes);
        public bool DisposeStageOnFailure { get; set; } = true;

        public void Dispose()
        {
            if (DisposeStageOnFailure && Directory.Exists(StageDirectory))
            {
                Directory.Delete(StageDirectory, recursive: true);
            }
        }
    }

    private sealed class ExternalExtensionException(
        string packageId,
        string version,
        string code,
        string message) : Exception(message)
    {
        public string PackageId { get; } = packageId;
        public string Version { get; } = version;
        public string Code { get; } = code;
    }
}

internal sealed class PackageLoadContext(
    string packageDirectory,
    ImmutableDictionary<string, ExternalExtensionPackageLoader.VerifiedAssemblyImage> assemblies)
    : AssemblyLoadContext(isCollectible: true)
{
    private static readonly Assembly SdkAssembly = typeof(IExtensionModule).Assembly;
    private static readonly ImmutableHashSet<string> PlatformAssemblies = BuildPlatformAssemblies();
    private readonly string _packageDirectory = packageDirectory;
    private ImmutableDictionary<string, ExternalExtensionPackageLoader.VerifiedAssemblyImage> _assemblies =
        assemblies;

    private static ImmutableHashSet<string> BuildPlatformAssemblies()
    {
        var frameworkRoots = new[]
        {
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            Path.GetDirectoryName(typeof(IHostedService).Assembly.Location)!
        }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => frameworkRoots.Contains(Path.GetDirectoryName(path)!))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsPlatformAssembly(string? name) =>
        PlatformAssemblies.Contains(name ?? string.Empty);

    internal Assembly LoadEntryAssembly(
        ExternalExtensionPackageLoader.VerifiedAssemblyImage image)
    {
        return LoadVerifiedImage(image);
    }

    internal void UnloadAndDelete()
    {
        Unload();
        _assemblies = ImmutableDictionary<string, ExternalExtensionPackageLoader.VerifiedAssemblyImage>.Empty;
        if (Directory.Exists(_packageDirectory))
        {
            Directory.Delete(_packageDirectory, recursive: true);
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(
                assemblyName.Name,
                ExtensionSdkVersions.Identity.Value,
                StringComparison.Ordinal))
        {
            return AssemblyName.ReferenceMatchesDefinition(assemblyName, SdkAssembly.GetName())
                ? SdkAssembly
                : throw new FileLoadException("The extension requested a different SDK identity.");
        }

        if (PlatformAssemblies.Contains(assemblyName.Name ?? string.Empty))
        {
            return null;
        }

        if (!_assemblies.TryGetValue(assemblyName.Name ?? string.Empty, out var image))
        {
            throw new FileNotFoundException(
                $"Private dependency '{assemblyName.Name}' is not present in the staged package.");
        }
        if (!AssemblyName.ReferenceMatchesDefinition(assemblyName, image.Identity))
        {
            throw new FileLoadException(
                $"Private dependency '{assemblyName.Name}' does not match its validated identity.");
        }
        return LoadVerifiedImage(image);
    }

    private Assembly LoadVerifiedImage(
        ExternalExtensionPackageLoader.VerifiedAssemblyImage image)
    {
        using var assemblyStream = new MemoryStream(image.AssemblyBytes, writable: false);
        if (image.SymbolsBytes is not { } symbols)
        {
            return LoadFromStream(assemblyStream);
        }

        using var symbolsStream = new MemoryStream(symbols, writable: false);
        return LoadFromStream(assemblyStream, symbolsStream);
    }
}
