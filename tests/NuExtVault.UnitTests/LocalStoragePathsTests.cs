using NuExtVault.Storage;

namespace NuExtVault.UnitTests;

public sealed class LocalStoragePathsTests
{
    [Fact]
    public void Default_roots_use_the_current_and_legacy_local_app_data_directories()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(
            Path.Combine(localAppData, "nuextvault"),
            LocalStoragePaths.DefaultRoot);
        Assert.Equal(
            Path.Combine(localAppData, "nuget-test-server"),
            LocalStoragePaths.LegacyDefaultRoot);
        Assert.True(Path.IsPathFullyQualified(LocalStoragePaths.DefaultRoot));
    }

    [Fact]
    public void Default_resolution_requires_an_explicit_root_when_neither_root_exists()
    {
        using var root = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(
            () => LocalStoragePaths.ResolveDefaultRoot(root.Path));

        Assert.Contains("--storage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("concurrent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_resolution_adopts_the_legacy_root_when_it_alone_exists()
    {
        using var root = new TemporaryDirectory();
        var legacy = Path.Combine(root.Path, "nuget-test-server");
        Directory.CreateDirectory(legacy);

        Assert.Equal(legacy, LocalStoragePaths.ResolveDefaultRoot(root.Path));
    }

    [Fact]
    public void Default_resolution_requires_explicit_storage_when_only_new_root_exists()
    {
        using var root = new TemporaryDirectory();
        var current = Path.Combine(root.Path, "nuextvault");
        Directory.CreateDirectory(current);

        var exception = Assert.Throws<InvalidOperationException>(
            () => LocalStoragePaths.ResolveDefaultRoot(root.Path));

        Assert.Contains("--storage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_resolution_rejects_ambiguous_legacy_and_new_roots()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "nuget-test-server"));
        Directory.CreateDirectory(Path.Combine(root.Path, "nuextvault"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => { LocalStoragePaths.ResolveDefaultRoot(root.Path); });

        Assert.Contains("--storage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nuget-test-server", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nuextvault", exception.Message, StringComparison.Ordinal);
    }
}
