using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Kernel.Owners.PackageMetadata;

internal static class PackageMetadataDocumentBuilder
{
    public static ImmutableArray<PackageTypeDocument> CreatePackageTypes(
        CapabilityPackageMetadata package) =>
        [
            .. package.EffectivePackageTypes.Select(
                type => new PackageTypeDocument(type.Name, type.Version))
        ];
}
