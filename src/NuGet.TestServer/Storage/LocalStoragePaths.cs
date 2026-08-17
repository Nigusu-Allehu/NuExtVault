namespace NuGet.TestServer.Storage;

public static class LocalStoragePaths
{
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nuget-test-server");
}
