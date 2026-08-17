using NuGet.TestServer.Storage;

namespace NuGet.TestServer.UnitTests;

public sealed class LocalStoragePathsTests
{
    [Fact]
    public void Default_root_uses_the_operating_system_local_app_data_directory()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(
            Path.Combine(localAppData, "nuget-test-server"),
            LocalStoragePaths.DefaultRoot);
        Assert.True(Path.IsPathFullyQualified(LocalStoragePaths.DefaultRoot));
    }
}
