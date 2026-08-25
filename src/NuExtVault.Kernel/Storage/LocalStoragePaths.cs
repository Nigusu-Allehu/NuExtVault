namespace NuExtVault.Storage;

public static class LocalStoragePaths
{
    public static string DefaultRoot =>
        Path.Combine(LocalAppData, "nuextvault");

    public static string LegacyDefaultRoot =>
        Path.Combine(LocalAppData, "nuget-test-server");

    public static string ResolveDefaultRoot() => ResolveDefaultRoot(LocalAppData);

    internal static string ResolveDefaultRoot(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        var current = Path.Combine(Path.GetFullPath(localAppData), "nuextvault");
        var legacy = Path.Combine(Path.GetFullPath(localAppData), "nuget-test-server");
        var currentExists = Directory.Exists(current);
        var legacyExists = Directory.Exists(legacy);
        if (currentExists && legacyExists)
        {
            throw new InvalidOperationException(
                $"Both the current default storage root '{current}' and the legacy root " +
                $"'{legacy}' exist. Refusing to choose or merge them; pass --storage with " +
                "the intended repository path.");
        }

        if (!legacyExists)
        {
            throw new InvalidOperationException(
                $"Legacy default storage root '{legacy}' does not exist. A concurrent older " +
                $"server could create or use that root while this version selects '{current}', " +
                "so automatic selection is unsafe; pass --storage with the intended repository " +
                "path.");
        }

        return legacy;
    }

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
