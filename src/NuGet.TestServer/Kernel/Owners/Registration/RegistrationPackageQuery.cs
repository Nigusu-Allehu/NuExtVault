using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Owners.Registration;

internal interface IRegistrationPackageQuery
{
    ValueTask<IReadOnlyList<CapabilityPackageMetadata>> FindByIdAsync(
        string packageId,
        CancellationToken token);

    ValueTask<CapabilityPackageMetadata?> FindLeafAsync(
        string packageId,
        string version,
        CancellationToken token);
}

internal sealed class RegistrationPackageQuery(IPackageReadCapability packages)
    : IRegistrationPackageQuery
{
    public ValueTask<IReadOnlyList<CapabilityPackageMetadata>> FindByIdAsync(
        string packageId,
        CancellationToken token) =>
        packages.FindReadableStoredByIdAsync(
            packageId,
            PackageResourceClass.Registration,
            token);

    public ValueTask<CapabilityPackageMetadata?> FindLeafAsync(
        string packageId,
        string version,
        CancellationToken token) =>
        packages.FindReadableAsync(
            packageId,
            version,
            PackageResourceClass.Registration,
            token);
}
