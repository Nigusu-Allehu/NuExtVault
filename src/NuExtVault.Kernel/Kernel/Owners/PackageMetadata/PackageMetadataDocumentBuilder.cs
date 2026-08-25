using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Kernel.Capabilities;

namespace NuExtVault.Kernel.Owners.PackageMetadata;

internal static class PackageMetadataDocumentBuilder
{
    public static ImmutableArray<PackageTypeDocument> CreatePackageTypes(
        CapabilityPackageMetadata package) =>
        [
            .. package.EffectivePackageTypes.Select(
                type => new PackageTypeDocument(type.Name, type.Version))
        ];
}
