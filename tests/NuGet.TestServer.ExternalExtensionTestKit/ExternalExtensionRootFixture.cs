namespace NuGet.TestServer.ExternalExtensionTestKit;

/// <summary>
/// Step 20 tests-first red phase helper. Manages one or more temporary
/// "configured root" directories that would be passed as
/// `ExternalExtensionConfiguration.Roots`, each containing installed `.nupkg`
/// files. Disposal deletes every root, regardless of what the (not yet
/// implemented) loader staged from them.
/// </summary>
public sealed class ExternalExtensionRootFixture : IDisposable
{
    private readonly string[] _roots;

    private ExternalExtensionRootFixture(string[] roots) => _roots = roots;

    public IReadOnlyList<string> Roots => _roots;

    public static ExternalExtensionRootFixture CreateRoots(int count = 1)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var roots = new string[count];
        for (var index = 0; index < count; index++)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "NuGet.TestServer.ExternalExtensions.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            roots[index] = root;
        }

        return new ExternalExtensionRootFixture(roots);
    }

    /// <summary>Writes one `.nupkg` file into a root directory and returns its
    /// full path.</summary>
    public string WritePackage(int rootIndex, string fileName, byte[] nupkgBytes)
    {
        var path = Path.Combine(_roots[rootIndex], fileName);
        File.WriteAllBytes(path, nupkgBytes);
        return path;
    }

    public string WritePackage(string fileName, byte[] nupkgBytes) =>
        WritePackage(0, fileName, nupkgBytes);

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering handle from a collectible ALC
                // that has not finished unloading must not fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
