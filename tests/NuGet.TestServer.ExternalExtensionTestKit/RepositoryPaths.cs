using System.Diagnostics;

namespace NuGet.TestServer.ExternalExtensionTestKit;

/// <summary>
/// Step 20 tests-first red phase helper. Locates the repository root and shells
/// out to `dotnet pack` for fixtures that must be genuinely, separately compiled
/// (mirrors the pattern already proven by
/// <c>NuGet.TestServer.Extensions.Sdk.Tests.TestPaths</c>).
/// </summary>
public static class RepositoryPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ArtifactsDirectory { get; } =
        Path.Combine(RepositoryRoot, "artifacts", "step20-external-extension-tests");

    public static string SdkFixtureProjectPath { get; } = Path.Combine(
        "tests",
        "NuGet.TestServer.SdkFixture",
        "NuGet.TestServer.SdkFixture.csproj");

    public static string ForbiddenReferenceFixtureProjectPath { get; } = Path.Combine(
        "tests",
        "NuGet.TestServer.ForbiddenReferenceFixture",
        "NuGet.TestServer.ForbiddenReferenceFixture.csproj");

    /// <summary>
    /// The optional Package Staging extension. It lives under <c>src</c> because it is a
    /// first-class, independently packable extension, not a test fixture; tests only
    /// pack it the way an administrator would install it.
    /// </summary>
    public static string PackageStagingProjectPath { get; } = Path.Combine(
        "src",
        "NuGet.TestServer.Extensions.PackageStaging",
        "NuGet.TestServer.Extensions.PackageStaging.csproj");

    /// <summary>
    /// Serializes fixture <c>dotnet</c> invocations. Several collections pack their own
    /// extension into the same artifacts and package folders, and concurrent restores of
    /// one package folder are not safe, so packs are serialized within the process and
    /// across processes. The cross-process gate is a lock file rather than a mutex
    /// because the critical section spans an <c>await</c>.
    /// </summary>
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    private static string PackLockPath => Path.Combine(ArtifactsDirectory, ".pack.lock");

    public static async Task<ProcessResult> DotNetAsync(params string[] arguments)
    {
        Directory.CreateDirectory(ArtifactsDirectory);
        await ProcessGate.WaitAsync();
        try
        {
            using var gate = await AcquireLockFileAsync();
            return await RunAsync(arguments);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    private static async Task<FileStream> AcquireLockFileAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (true)
        {
            try
            {
                return new FileStream(
                    PackLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static async Task<ProcessResult> RunAsync(string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["NUGET_PACKAGES"] =
            Path.Combine(ArtifactsDirectory, "packages");
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output + error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NuGet.TestServer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("The repository root was not found.");
    }
}

public sealed record ProcessResult(int ExitCode, string Output);
