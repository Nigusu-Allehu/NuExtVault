using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.FlatContainer;
using NuExtVault.Extensions.Operations;
using NuExtVault.Extensions.PackageManagement;
using NuExtVault.Extensions.Search;
using NuExtVault.Extensions.Registration;

namespace NuExtVault.Extensions.Official;

/// <summary>
/// The official extension modules that ship in the box. They are contributed through
/// the same module seam a separately compiled module uses: the host merges their
/// manifests into the catalog, their selections into profiles, and their operation
/// owners into the registry without naming any individual module.
/// </summary>
internal static class OfficialExtensionModules
{
    public static ImmutableArray<IExtensionModule> All { get; } =
    [
        new FlatContainerModule(),
        new OperationsModule(),
        new PackageManagementModule(),
        new RegistrationModule(),
        new SearchModule()
    ];

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
