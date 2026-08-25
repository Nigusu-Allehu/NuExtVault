namespace NuExtVault.Storage;

public static class LocalStoragePaths
{
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nuextvault");
}
