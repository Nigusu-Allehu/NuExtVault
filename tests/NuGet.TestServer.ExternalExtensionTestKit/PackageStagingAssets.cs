namespace NuGet.TestServer.ExternalExtensionTestKit;

/// <summary>
/// Packs the optional Package Staging extension exactly the way an administrator would
/// obtain it — <c>dotnet pack</c> of the independently packable project under
/// <c>src</c> — and returns the signed-package inputs the trusted loader consumes.
/// </summary>
public static class PackageStagingAssets
{
    public const string Id = "NuTest.PackageStaging";
    public const string Version = "1.0.0";
    public const string Publisher = "NuTest";
    public const string EntryAssemblyFileName = "NuTest.PackageStaging.dll";
    public const string EntryType = "NuTest.PackageStaging.PackageStagingModule";

    public static async Task<ContosoFlavorsAssets> BuildAsync(
        string outputName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        var output = Path.Combine(RepositoryPaths.ArtifactsDirectory, outputName);
        var result = await RepositoryPaths.DotNetAsync(
            "pack",
            RepositoryPaths.PackageStagingProjectPath,
            "--configuration",
            "Release",
            "--output",
            Path.GetRelativePath(RepositoryPaths.RepositoryRoot, output));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to pack the Package Staging extension:{Environment.NewLine}{result.Output}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var entries = ExternalExtensionPackageBuilder.ReadEntries(
            await File.ReadAllBytesAsync(
                Path.Combine(output, $"{Id}.{Version}.nupkg"),
                cancellationToken));
        return new ContosoFlavorsAssets(
            Id,
            Version,
            Publisher,
            EntryAssemblyFileName,
            EntryType,
            entries[ExternalExtensionPackageBuilder.ManifestEntryName],
            entries[$"{ExternalExtensionPackageBuilder.LibDirectory}{EntryAssemblyFileName}"],
            null,
            new Dictionary<string, byte[]>());
    }
}
