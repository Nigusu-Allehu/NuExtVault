using System.Collections.Concurrent;
using System.Security.Cryptography;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Packages;

internal sealed class PolicyPackageHandleRegistry
{
    private readonly ConcurrentDictionary<string, TestPackage> _packages =
        new(StringComparer.Ordinal);

    public PolicyPackageLease Register(TestPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var handle = new PolicyPackageHandle(Guid.NewGuid().ToString("N"));
        if (!_packages.TryAdd(handle.Value, package))
        {
            throw new InvalidOperationException("A unique package policy handle could not be issued.");
        }

        return new PolicyPackageLease(this, handle);
    }

    public TestPackage Resolve(PolicyPackageHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return _packages.TryGetValue(handle.Value, out var package)
            ? package
            : throw new InvalidOperationException("The package policy handle is invalid or expired.");
    }

    private void Release(PolicyPackageHandle handle) => _packages.TryRemove(handle.Value, out _);

    internal sealed class PolicyPackageLease(
        PolicyPackageHandleRegistry owner,
        PolicyPackageHandle handle) : IDisposable
    {
        private int _disposed;

        public PolicyPackageHandle Handle { get; } = handle;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(Handle);
            }
        }
    }
}

internal sealed class PackagePolicyInspectionService(
    PolicyPackageHandleRegistry handles,
    SupplyChainOptions options,
    IPackagePolicyScanner scanner)
{
    public async ValueTask<PackageSignatureInspection> InspectSignatureAsync(
        PolicyPackageHandle handle,
        CancellationToken cancellationToken)
    {
        var package = handles.Resolve(handle);
        try
        {
            await using var stream = package.OpenReadStream();
            using var reader = new PackageArchiveReader(stream, leaveStreamOpen: false);
            var signature = await reader.GetPrimarySignatureAsync(cancellationToken);
            if (signature is null)
            {
                return new(
                    SignatureInspectionOutcome.Unsigned,
                    options.RequireSignedPackages
                        ? "A package signature is required."
                        : "Package is unsigned and policy allows unsigned packages.");
            }

            var verifier = new PackageSignatureVerifier([new IntegrityVerificationProvider()]);
            var settings = SignedPackageVerifierSettings.GetAcceptModeDefaultPolicy();
            var result = await verifier.VerifySignaturesAsync(reader, settings, cancellationToken);
            return new(
                result.IsValid
                    ? SignatureInspectionOutcome.Valid
                    : SignatureInspectionOutcome.Invalid,
                result.IsValid
                    ? "NuGet package signature and signed content integrity are valid."
                    : "NuGet package signature or signed content integrity is invalid.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or SignatureException or CryptographicException)
        {
            return new(
                SignatureInspectionOutcome.Invalid,
                $"NuGet package signature is invalid: {exception.Message}");
        }
    }

    public async ValueTask<PackageScannerInspection> ScanAsync(
        PolicyPackageHandle handle,
        CancellationToken cancellationToken)
    {
        var result = await scanner.ScanAsync(handles.Resolve(handle), cancellationToken);
        return new(
            result.Outcome switch
            {
                PackageScanOutcome.Clean => PackageScannerInspectionOutcome.Clean,
                PackageScanOutcome.Malicious => PackageScannerInspectionOutcome.Malicious,
                _ => PackageScannerInspectionOutcome.Inconclusive
            },
            result.Detail);
    }
}
