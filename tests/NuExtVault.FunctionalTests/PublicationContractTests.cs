using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;

namespace NuExtVault.FunctionalTests;

public sealed class PublicationContractTests
{
    private const string PackageId = "NuExtVault";
    private const string PackageVersion = "1.0.0";
    private const string RepositoryUrl = "https://github.com/Nigusu-Allehu/NuExtVault";

    [Fact]
    public void Only_the_cli_project_defines_a_global_tool_package()
    {
        var projects = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(path => (
                Path: path,
                Project: XDocument.Load(path)))
            .ToArray();

        var toolProjects = projects
            .Where(project => Property(project.Project, "PackAsTool") == "true")
            .Select(project => Path.GetRelativePath(RepositoryRoot, project.Path))
            .ToArray();

        Assert.Equal(
            [Path.Combine("src", "NuExtVault.Cli", "NuExtVault.Cli.csproj")],
            toolProjects);
        Assert.All(
            projects.Where(project => !toolProjects.Contains(
                Path.GetRelativePath(RepositoryRoot, project.Path),
                StringComparer.Ordinal)),
            project => Assert.NotEqual(PackageId, Property(project.Project, "PackageId")));
    }

    [Fact]
    public async Task Tool_package_has_the_publication_identity_metadata_and_assets()
    {
        using var directory = TemporaryDirectory.Create();
        var packagePath = await PackToolAsync(directory.Path);

        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream);
        var metadata = Assert.Single(
            nuspec.Descendants(),
            element => element.Name.LocalName == "metadata");

        Assert.Equal(PackageId, Element(metadata, "id"));
        Assert.Equal(PackageVersion, Element(metadata, "version"));
        Assert.Equal("Nigusu Solomon Yenework", Element(metadata, "authors"));
        Assert.Equal(RepositoryUrl, Element(metadata, "projectUrl"));
        var license = Assert.Single(
            metadata.Elements(),
            element => element.Name.LocalName == "license");
        Assert.Equal("file", license.Attribute("type")?.Value);
        Assert.Equal("LICENSE", license.Value);
        Assert.Equal("README.md", Element(metadata, "readme"));
        Assert.Contains("nuget", Element(metadata, "tags"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server", Element(metadata, "description"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.0", Element(metadata, "releaseNotes"), StringComparison.Ordinal);

        var repository = Assert.Single(
            metadata.Elements(),
            element => element.Name.LocalName == "repository");
        Assert.Equal("git", repository.Attribute("type")?.Value);
        Assert.Equal(RepositoryUrl + ".git", repository.Attribute("url")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(repository.Attribute("commit")?.Value));

        var files = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("LICENSE", files);
        Assert.Contains("README.md", files);
        Assert.Contains("tools/net10.0/any/DotnetToolSettings.xml", files);
        Assert.DoesNotContain(
            files,
            file => file.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        var settingsEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName == "tools/net10.0/any/DotnetToolSettings.xml");
        using var settingsStream = settingsEntry.Open();
        var settings = XDocument.Load(settingsStream);
        Assert.Equal(
            "nuextvault",
            settings.Descendants()
                .Single(element => element.Name.LocalName == "Command")
                .Attribute("Name")?.Value);
    }

    [Fact]
    public async Task Exact_package_installs_starts_real_kestrel_and_uninstalls()
    {
        using var directory = TemporaryDirectory.Create();
        var feed = Path.Combine(directory.Path, "feed");
        var tools = Path.Combine(directory.Path, "tools");
        var storage = Path.Combine(directory.Path, "storage");
        var config = Path.Combine(directory.Path, "NuGet.Config");
        Directory.CreateDirectory(feed);
        var packagePath = await PackToolAsync(feed);
        await File.WriteAllTextAsync(
            config,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="ExactPackage" value="{feed}" />
              </packageSources>
            </configuration>
            """);

        var install = await RunAsync(
            "dotnet",
            [
                "tool", "install",
                "--tool-path", tools,
                PackageId,
                "--configfile", config,
                "--version", PackageVersion,
                "--no-cache"
            ],
            directory.Path);
        Assert.True(install.ExitCode == 0, install.Output);

        var executable = Path.Combine(
            tools,
            OperatingSystem.IsWindows() ? "nuextvault.exe" : "nuextvault");
        Assert.True(File.Exists(executable), $"Tool command was not installed at '{executable}'.");
        Assert.Equal(
            Path.GetFullPath(packagePath),
            Path.GetFullPath(Assert.Single(Directory.GetFiles(feed, "*.nupkg"))));

        var port = GetAvailablePort();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }.WithArguments(["start", "--port", port.ToString(), "--storage", storage]))!;

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync("/health/ready", timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        break;
                    }
                }
                catch (HttpRequestException) when (!timeout.IsCancellationRequested)
                {
                }

                await Task.Delay(100, timeout.Token);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var uninstall = await RunAsync(
            "dotnet",
            ["tool", "uninstall", "--tool-path", tools, PackageId],
            directory.Path);
        Assert.True(uninstall.ExitCode == 0, uninstall.Output);
        Assert.False(File.Exists(executable), "Tool command remains after uninstall.");
    }

    [Fact]
    public void Release_workflow_is_tag_or_manual_only_and_uses_protected_oidc_publish()
    {
        var path = Path.Combine(RepositoryRoot, ".github", "workflows", "release.yml");
        Assert.True(File.Exists(path), "Missing .github/workflows/release.yml.");
        var workflow = File.ReadAllText(path);

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("tags:", workflow, StringComparison.Ordinal);
        Assert.Contains("- \"v*\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Equal(1, Count(workflow, "dotnet pack "));
        Assert.Contains("--warnaserror", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test tests/NuExtVault.Extensions.Sdk.Tests/NuExtVault.Extensions.Sdk.Tests.csproj",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test tests/NuExtVault.UnitTests/NuExtVault.UnitTests.csproj",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test tests/NuExtVault.FunctionalTests/NuExtVault.FunctionalTests.csproj",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("Validate tag and package version", workflow, StringComparison.Ordinal);
        Assert.Contains("Smoke test exact package", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: actions/download-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: nuget.org", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: NuGet/login@v1", workflow, StringComparison.Ordinal);
        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
        Assert.Contains("steps.login.outputs.NUGET_API_KEY", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY:", workflow, StringComparison.Ordinal);

        var publish = workflow[workflow.IndexOf("  publish:", StringComparison.Ordinal)..];
        Assert.Contains("id-token: write", publish, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "id-token: write",
            workflow[..workflow.IndexOf("  publish:", StringComparison.Ordinal)],
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot => DocumentationContractTests.RepositoryRoot;

    private static async Task<string> PackToolAsync(string output)
    {
        Directory.CreateDirectory(output);
        var result = await RunAsync(
            "dotnet",
            [
                "pack",
                Path.Combine(RepositoryRoot, "src", "NuExtVault.Cli", "NuExtVault.Cli.csproj"),
                "--configuration", "Release",
                "-p:TreatWarningsAsErrors=true",
                "--output", output
            ],
            RepositoryRoot);
        Assert.True(result.ExitCode == 0, result.Output);

        var packages = Directory.GetFiles(output, "*.nupkg");
        return Assert.Single(packages);
    }

    private static string? Property(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();

    private static string Element(XElement parent, string name) =>
        Assert.Single(parent.Elements(), element => element.Name.LocalName == name).Value;

    private static int Count(string value, string search)
    {
        var count = 0;
        for (var index = 0;
             (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0;
             index += search.Length)
        {
            count++;
        }

        return count;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }.WithArguments(arguments))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuExtVault.PublicationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    internal static ProcessStartInfo WithArguments(
        this ProcessStartInfo startInfo,
        IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
