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

    public static async Task<ProcessResult> DotNetAsync(params string[] arguments)
    {
        Directory.CreateDirectory(ArtifactsDirectory);
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
