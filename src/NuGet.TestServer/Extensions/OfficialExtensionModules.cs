using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Extensions.FlatContainer;

namespace NuGet.TestServer.Extensions;

/// <summary>
/// The official extension modules that ship in the box. They are contributed through
/// the same module seam a separately compiled module uses: the host merges their
/// manifests into the catalog, their selections into profiles, and their operation
/// owners into the registry without naming any individual module.
/// </summary>
internal static class OfficialExtensionModules
{
    public static ImmutableArray<IExtensionModule> All { get; } = [new FlatContainerModule()];

    public static ImmutableArray<ExtensionManifest> Manifests { get; } =
        [.. All.Select(module => module.Contribution.Manifest)];

    /// <summary>
    /// The profile selections that activate the official modules. Capability grants stay
    /// explicit in each profile, so a module request that a profile does not grant fails
    /// composition instead of escalating silently.
    /// </summary>
    public static ImmutableArray<ExtensionSelection> Selections { get; } =
        [.. All.Select(module => module.Contribution.Selection)];
}
