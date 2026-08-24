using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class DocumentationExampleTests
{
    private static readonly string[] ExpectedIds =
    [
        "user-01-pack-install",
        "user-01-start",
        "user-01-start-output",
        "user-01-readiness",
        "user-01-cleanup",
        "user-02-seed",
        "user-02-nuget-config",
        "user-02-push",
        "user-02-restore",
        "user-02-list",
        "user-02-unlist",
        "user-02-relist",
        "user-02-delete",
        "user-03-generated-package",
        "user-03-parallel-isolation",
        "user-04-production-start",
        "user-04-production-route-check",
        "user-04-cleanup",
        "user-05-fault-rule",
        "user-05-fault-output",
        "user-05-request-history",
        "user-05-cleanup",
        "user-06-liveness",
        "user-06-readiness",
        "user-06-backup",
        "user-06-restore",
        "user-07-package-layout",
        "user-07-trust-root",
        "user-07-staging-grants",
        "user-07-staging-start",
        "user-07-staging-routes",
        "user-08-limit-table",
        "user-08-limits",
        "contrib-01-system-diagram",
        "contrib-01-assembly-dag",
        "contrib-02-request-sequence",
        "contrib-03-evidence-inventories",
        "contrib-04-public-capabilities",
        "contrib-06-sdk-example",
        "contrib-06-version-table",
        "contrib-08-validation-commands"
    ];

    private static readonly HashSet<string> ReferenceIds =
    [
        "user-01-start-output",
        "user-02-nuget-config",
        "user-04-production-start",
        "user-05-fault-output",
        "user-07-package-layout",
        "user-07-trust-root",
        "user-07-staging-grants",
        "user-07-staging-start",
        "user-07-staging-routes",
        "user-08-limit-table",
        "user-08-limits",
        "contrib-01-system-diagram",
        "contrib-01-assembly-dag",
        "contrib-02-request-sequence",
        "contrib-03-evidence-inventories",
        "contrib-04-public-capabilities",
        "contrib-06-version-table",
        "contrib-08-validation-commands"
    ];

    [Fact]
    public void Every_documented_example_has_automated_evidence()
    {
        var examples = Examples();

        Assert.Equal(
            ExpectedIds.Order(StringComparer.Ordinal),
            examples.Keys.Order(StringComparer.Ordinal));
        Assert.All(examples.Values, example =>
            Assert.Equal(
                ReferenceIds.Contains(example.Id) ? "reference" : "executable",
                example.Evidence));
    }

    [Fact]
    public void Reference_examples_match_implemented_contracts()
    {
        var examples = Examples();
        Assert.Contains("Source:      http://127.0.0.1:<port>/v3/index.json",
            examples["user-01-start-output"].Content, StringComparison.Ordinal);
        var productionStart = examples["user-04-production-start"].Content;
        Assert.StartsWith("$env:NUTEST_IDENTITIES = \"{{IDENTITY_JSON}}\"",
            productionStart, StringComparison.Ordinal);
        Assert.Contains("& \"{{TOOL_COMMAND}}\" start --production",
            productionStart, StringComparison.Ordinal);
        Assert.Contains("--identity-config-env NUTEST_IDENTITIES",
            productionStart, StringComparison.Ordinal);
        Assert.Contains("--trusted-proxy 127.0.0.1",
            productionStart, StringComparison.Ordinal);
        Assert.Contains("--port \"{{PORT}}\"", productionStart, StringComparison.Ordinal);
        Assert.Contains("--storage \"{{STORAGE}}\"", productionStart, StringComparison.Ordinal);

        var config = XDocument.Parse(examples["user-02-nuget-config"].Content);
        var source = config.Descendants("add").Single();
        Assert.Equal("TestServer", source.Attribute("key")?.Value);
        Assert.Equal("{{BASE_URL}}/v3/index.json", source.Attribute("value")?.Value);
        Assert.Equal("true", source.Attribute("allowInsecureConnections")?.Value);

        Assert.Equal(
            ["503", "503", "200"],
            Lines(examples["user-05-fault-output"].Content));

        Assert.Equal(
            """
            Contoso.Extension.1.2.3.nupkg
            ├── Contoso.Extension.nuspec
            ├── extension-manifest.json
            ├── extension-package.json
            ├── extension-attestation.json
            └── lib/net10.0/Contoso.Extension.dll
            """.ReplaceLineEndings(),
            examples["user-07-package-layout"].Content.ReplaceLineEndings());

        using var trust = JsonDocument.Parse(examples["user-07-trust-root"].Content);
        Assert.Equal(4, trust.RootElement.EnumerateObject().Count());
        Assert.Equal("Contoso", trust.RootElement.GetProperty("publisher").GetString());
        Assert.Equal(
            "contoso-extension-signing-2026",
            trust.RootElement.GetProperty("keyId").GetString());
        Assert.Equal("ES256", trust.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal(
            "<base64 DER SubjectPublicKeyInfo>",
            trust.RootElement.GetProperty("subjectPublicKeyInfoBase64").GetString());
        var grants =
            new[] { "host.clock.read", "extension-state.read", "extension-state.write",
                    "packages.content.write-staged", "publication.request" };
        Assert.Equal(
            grants,
            Lines(examples["user-07-staging-grants"].Content));

        var stagingStart = examples["user-07-staging-start"].Content;
        Assert.StartsWith(
            "& \"{{TOOL_COMMAND}}\" start --port \"{{PORT}}\" --storage \"{{STORAGE}}\"",
            stagingStart,
            StringComparison.Ordinal);
        Assert.Contains("--extension-root \"{{EXTENSION_ROOT}}\"", stagingStart, StringComparison.Ordinal);
        Assert.Contains("--extension-trust-root \"{{TRUST_ROOT}}\"", stagingStart, StringComparison.Ordinal);
        Assert.Equal(5, stagingStart.Split("--extension-grant", StringSplitOptions.None).Length - 1);
        Assert.All(grants, grant =>
            Assert.Contains($"--extension-grant {grant}", stagingStart, StringComparison.Ordinal));

        Assert.Equal(
            [
                "PUT  /staging/groups/{groupId}",
                "GET  /staging/groups",
                "GET  /staging/groups/{groupId}",
                "PUT  /staging/groups/{groupId}/packages",
                "PUT  /staging/groups/{groupId}/packages/{packageId}/{version}/symbols",
                "GET  /staging/groups/{groupId}/packages/{packageId}/{version}",
                "POST /staging/groups/{groupId}/packages/{packageId}/{version}/promote",
                "POST /staging/groups/{groupId}/packages/{packageId}/{version}/reject",
                "POST /staging/groups/{groupId}/expire"
            ],
            Lines(examples["user-07-staging-routes"].Content));

        var limits = examples["user-08-limit-table"].Content;
        Assert.Equal(
            [
                $"HTTP request body                 {PackageTransferLimits.DefaultMaxRequestBodyBytes / 1024 / 1024} MiB",
                $"Compressed package               {PackageTransferLimits.DefaultMaxPackageBytes / 1024 / 1024} MiB",
                $"Archive entries                   {PackageTransferLimits.DefaultMaxArchiveEntries.ToString("N0", CultureInfo.InvariantCulture)}",
                $"One expanded archive entry        {PackageTransferLimits.DefaultMaxArchiveEntryBytes / 1024 / 1024} MiB",
                $"Total expanded archive content   {PackageTransferLimits.DefaultMaxExpandedArchiveBytes / 1024 / 1024} MiB",
                $"Request history                   {RuntimeStateConfiguration.DefaultRequestHistoryCapacity.ToString("N0", CultureInfo.InvariantCulture)}",
                $"Fault rules                          {RuntimeStateConfiguration.DefaultFaultRuleCapacity}"
            ],
            Lines(limits));
        var limitCommand = examples["user-08-limits"].Content;
        Assert.Contains("--max-request-bytes 67108864", limitCommand, StringComparison.Ordinal);
        Assert.Contains("--max-package-bytes 52428800", limitCommand, StringComparison.Ordinal);
        Assert.Contains("--max-archive-entries 5000", limitCommand, StringComparison.Ordinal);
        Assert.Contains("--max-entry-bytes 16777216", limitCommand, StringComparison.Ordinal);
        Assert.Contains("--max-expanded-bytes 268435456", limitCommand, StringComparison.Ordinal);

        Assert.Contains(
            "Client --> Gateway",
            examples["contrib-01-system-diagram"].Content,
            StringComparison.Ordinal);
        Assert.Equal(
            ProductProjectEdges(),
            Lines(examples["contrib-01-assembly-dag"].Content));
        Assert.Contains(
            "Gateway->>Dispatcher",
            examples["contrib-02-request-sequence"].Content,
            StringComparison.Ordinal);
        Assert.Equal(
            [
                "tests/NuGet.TestServer.UnitTests/Snapshots/operations.contract.txt",
                "tests/NuGet.TestServer.UnitTests/Snapshots/routes.contract.txt",
                "tests/NuGet.TestServer.UnitTests/Snapshots/resources.contract.txt",
                "tests/NuGet.TestServer.UnitTests/Snapshots/capabilities.contract.txt"
            ],
            Lines(examples["contrib-03-evidence-inventories"].Content));
        Assert.Equal(
            BrokerBackedPublicCapabilities(),
            Lines(examples["contrib-04-public-capabilities"].Content));
        Assert.Equal(
            PublicPackageVersions(),
            Lines(examples["contrib-06-version-table"].Content));
        Assert.Equal(
            [
                "dotnet restore NuGet.TestServer.slnx",
                "dotnet build NuGet.TestServer.slnx --no-restore --configuration Release --warnaserror",
                "dotnet test NuGet.TestServer.slnx --no-restore --no-build --configuration Release"
            ],
            Lines(examples["contrib-08-validation-commands"].Content));
        Assert.All(
            Lines(examples["contrib-03-evidence-inventories"].Content),
            relativePath => Assert.True(
                File.Exists(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))),
                $"Documented inventory '{relativePath}' does not exist."));
        Assert.All(
            Lines(examples["contrib-08-validation-commands"].Content),
            command => Assert.True(
                File.Exists(Path.Combine(RepositoryRoot, command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2])),
                $"Documented validation target in '{command}' does not exist."));
    }

    [Fact]
    public async Task Root_readme_quick_start_executes()
    {
        using var directory = TemporaryDirectory.Create();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(DocumentationContractTests.RootQuickStartCommand());
        process.StartInfo.Environment["LOCALAPPDATA"] = directory.Path;
        process.StartInfo.Environment["XDG_DATA_HOME"] = directory.Path;
        process.StartInfo.Environment["HOME"] = directory.Path;

        var output = new StringBuilder();
        var readiness = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            output.AppendLine(args.Data);
            const string prefix = "Readiness:";
            if (args.Data.StartsWith(prefix, StringComparison.Ordinal) &&
                Uri.TryCreate(args.Data[prefix.Length..].Trim(), UriKind.Absolute, out var uri))
            {
                readiness.TrySetResult(uri);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) output.AppendLine(args.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exited = process.WaitForExitAsync();
            var completed = await Task.WhenAny(
                readiness.Task,
                exited,
                Task.Delay(TimeSpan.FromMinutes(2)));
            Assert.True(completed == readiness.Task, output.ToString());

            using var client = new HttpClient();
            using var response = await client.GetAsync(await readiness.Task);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Public_sdk_example_compiles_and_runs_conformance()
    {
        const string id = "contrib-06-sdk-example";
        using var directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "Program.cs"),
            Example(id).Content);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "Example.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{EscapeXml(Path.Combine(RepositoryRoot, "src", "NuGet.TestServer.Extensions.Sdk", "NuGet.TestServer.Extensions.Sdk.csproj"))}" />
                <ProjectReference Include="{EscapeXml(Path.Combine(RepositoryRoot, "src", "NuGet.TestServer.Extensions.TestKit", "NuGet.TestServer.Extensions.TestKit.csproj"))}" />
                <ProjectReference Include="{EscapeXml(Path.Combine(RepositoryRoot, "tests", "NuGet.TestServer.SdkFixture", "NuGet.TestServer.SdkFixture.csproj"))}" />
              </ItemGroup>
            </Project>
            """);

        var result = await RunAsync(
            "dotnet",
            ["run", "--project", Path.Combine(directory.Path, "Example.csproj"), "--configuration", "Release"],
            directory.Path,
            TimeSpan.FromMinutes(3));
        Assert.True(result.ExitCode == 0, $"{id} failed:{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public async Task Programmatic_examples_compile_and_execute_exact_displayed_code()
    {
        foreach (var id in new[] { "user-03-generated-package", "user-03-parallel-isolation" })
        {
            using var directory = TemporaryDirectory.Create();
            await File.WriteAllTextAsync(Path.Combine(directory.Path, "Program.cs"), Example(id).Content);
            await File.WriteAllTextAsync(
                Path.Combine(directory.Path, "Example.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{EscapeXml(Path.Combine(RepositoryRoot, "src", "NuGet.TestServer", "NuGet.TestServer.csproj"))}" />
                    <PackageReference Include="NuGet.Protocol" Version="7.9.0" />
                  </ItemGroup>
                </Project>
                """);

            var result = await RunAsync(
                "dotnet",
                ["run", "--project", Path.Combine(directory.Path, "Example.csproj"), "--configuration", "Release"],
                directory.Path,
                TimeSpan.FromMinutes(3));
            Assert.True(result.ExitCode == 0, $"{id} failed:{Environment.NewLine}{result.Output}");
        }
    }

    [Fact]
    public async Task Http_and_control_examples_execute_exact_displayed_scripts()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var replacements = new Dictionary<string, string>
        {
            ["BASE_URL"] = server.HttpClient.BaseAddress!.ToString().TrimEnd('/')
        };

        Assert.Equal("200", (await RunPowerShellAsync("user-01-readiness", replacements)).Output.Trim());

        await server.Packages.AddAsync(
            TestPackageBuilder.Create("NuTest.Docs.Workflow", "1.0.0").Build());
        AssertSuccess(await RunPowerShellAsync("user-02-list", replacements));
        AssertSuccess(await RunPowerShellAsync("user-02-unlist", replacements));
        Assert.False(await IsListedAsync(server));
        AssertSuccess(await RunPowerShellAsync("user-02-relist", replacements));
        Assert.True(await IsListedAsync(server));
        AssertSuccess(await RunPowerShellAsync("user-02-delete", replacements));
        Assert.Null(await server.Packages.FindAsync("NuTest.Docs.Workflow", "1.0.0"));

        var fault = await RunPowerShellAsync("user-05-fault-rule", replacements);
        AssertSuccess(fault);
        Assert.Equal(["503", "503", "200"], Lines(fault.Output));
        AssertSuccess(await RunPowerShellAsync("user-05-request-history", replacements));
        AssertSuccess(await RunPowerShellAsync("user-05-cleanup", replacements));

        AssertSuccess(await RunPowerShellAsync("user-06-liveness", replacements));
        AssertSuccess(await RunPowerShellAsync("user-06-readiness", replacements));
    }

    [Fact]
    public async Task Production_route_example_executes_exact_displayed_script()
    {
        var security = ProductionSecurityConfiguration.Create(
            [
                new ProductionIdentityOptions(
                    "docs",
                    ["docs-key"],
                    [SecurityScope.Admin],
                    ["*"],
                    [])
            ]);
        await using var server = await NuGetTestServerHost.StartProductionAsync(security);
        var result = await RunPowerShellAsync(
            "user-04-production-route-check",
            new Dictionary<string, string>
            {
                ["BASE_URL"] = server.HttpClient.BaseAddress!.ToString().TrimEnd('/')
            });

        AssertSuccess(result);
        Assert.Equal(["404", "200"], Lines(result.Output));
    }

    [Fact]
    public async Task Push_restore_and_config_examples_execute_exact_displayed_text()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        using var directory = TemporaryDirectory.Create();
        var packagePath = Path.Combine(directory.Path, "NuTest.Docs.Workflow.1.0.0.nupkg");
        using (var package = TestPackageBuilder.Create("NuTest.Docs.Workflow", "1.0.0").Build())
        {
            await File.WriteAllBytesAsync(packagePath, package.Content);
        }

        var configPath = Path.Combine(directory.Path, "NuGet.config");
        await File.WriteAllTextAsync(
            configPath,
            Substitute(
                Example("user-02-nuget-config").Content,
                new Dictionary<string, string>
                {
                    ["BASE_URL"] = server.HttpClient.BaseAddress!.ToString().TrimEnd('/')
                }));
        var projectPath = Path.Combine(directory.Path, "Restore.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="NuTest.Docs.Workflow" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        var replacements = new Dictionary<string, string>
        {
            ["PACKAGE_PATH"] = packagePath,
            ["API_KEY"] = "ephemeral-docs-key",
            ["NUGET_CONFIG"] = configPath,
            ["PROJECT_PATH"] = projectPath,
            ["PACKAGES_DIRECTORY"] = Path.Combine(directory.Path, "packages")
        };

        AssertSuccess(await RunPowerShellAsync("user-02-push", replacements, directory.Path));
        AssertSuccess(await RunPowerShellAsync("user-02-restore", replacements, directory.Path));
        Assert.True(File.Exists(Path.Combine(directory.Path, "obj", "project.assets.json")));
    }

    [Fact]
    public async Task Cli_pack_install_backup_restore_and_cleanup_examples_execute()
    {
        using var directory = TemporaryDirectory.Create();
        var artifacts = Path.Combine(directory.Path, "artifacts");
        var tools = Path.Combine(directory.Path, "tools");
        var storage = Path.Combine(directory.Path, "storage");
        var common = new Dictionary<string, string>
        {
            ["ARTIFACTS"] = artifacts,
            ["TOOLS"] = tools,
            ["STORAGE"] = storage
        };

        AssertSuccess(await RunPowerShellAsync("user-01-pack-install", common, RepositoryRoot, TimeSpan.FromMinutes(5)));
        var installedTool = Path.Combine(
            tools,
            OperatingSystem.IsWindows() ? "nuget-test-server.exe" : "nuget-test-server");
        Assert.True(File.Exists(installedTool), $"Tool not installed at {installedTool}.");

        var quickStartPort = GetAvailablePort();
        var quickStartOutput = Path.Combine(directory.Path, "quick-start.out");
        var quickStart = await RunPowerShellAsync(
            "user-01-start",
            new Dictionary<string, string>
            {
                ["TOOL_COMMAND"] = installedTool,
                ["PORT"] = quickStartPort.ToString(),
                ["STORAGE"] = storage,
                ["OUTPUT_FILE"] = quickStartOutput,
                ["BASE_URL"] = $"http://127.0.0.1:{quickStartPort}"
            },
            timeout: TimeSpan.FromMinutes(2));
        AssertSuccess(quickStart);
        var expectedStartupLabels = Lines(Example("user-01-start-output").Content)
            .Select(line => line.Split(':', 2)[0] + ":");
        Assert.All(expectedStartupLabels, label =>
            Assert.Contains(label, quickStart.Output, StringComparison.Ordinal));
        Assert.Equal("200", Lines(quickStart.Output).Last());

        var productionStorage = Path.Combine(directory.Path, "production");
        var seedDirectory = Path.Combine(directory.Path, "seed");
        Directory.CreateDirectory(seedDirectory);
        using (var package = TestPackageBuilder.Create("Docs.Seeded", "1.0.0").Build())
        {
            await File.WriteAllBytesAsync(
                Path.Combine(seedDirectory, "Docs.Seeded.1.0.0.nupkg"),
                package.Content);
        }

        var seedPort = GetAvailablePort();
        var seed = await RunPowerShellAsync(
            "user-02-seed",
            new Dictionary<string, string>
            {
                ["TOOL_COMMAND"] = installedTool,
                ["PORT"] = seedPort.ToString(),
                ["STORAGE"] = Path.Combine(directory.Path, "seed-storage"),
                ["SEED_DIRECTORY"] = seedDirectory,
                ["BASE_URL"] = $"http://127.0.0.1:{seedPort}"
            },
            timeout: TimeSpan.FromMinutes(2));
        AssertSuccess(seed);
        Assert.Equal("200", Lines(seed.Output).Last());

        var backupStorage = Path.Combine(directory.Path, "backup-source");
        await using (var host = await NuGetTestServerHost.StartAsync(
            backupStorage,
            PackageTransferLimits.Default))
        {
            await host.Packages.AddAsync(
                TestPackageBuilder.Create("Docs.Backup", "1.0.0").Build());
        }

        var backup = Path.Combine(directory.Path, "backup.zip");
        var recovered = Path.Combine(directory.Path, "recovered");
        var backupValues = new Dictionary<string, string>
        {
            ["TOOL_COMMAND"] = installedTool,
            ["STORAGE"] = backupStorage,
            ["BACKUP"] = backup,
            ["RECOVERED"] = recovered
        };
        AssertSuccess(await RunPowerShellAsync("user-06-backup", backupValues));
        AssertSuccess(await RunPowerShellAsync("user-06-restore", backupValues));
        Assert.True(File.Exists(backup));
        Assert.True(Directory.Exists(recovered));

        var cleanupStorage = Path.Combine(directory.Path, "cleanup-storage");
        Directory.CreateDirectory(cleanupStorage);
        AssertSuccess(await RunPowerShellAsync(
            "user-01-cleanup",
            new Dictionary<string, string>
            {
                ["ARTIFACTS"] = artifacts,
                ["TOOLS"] = tools,
                ["STORAGE"] = cleanupStorage
            }));
        Assert.False(Directory.Exists(tools));

        Directory.CreateDirectory(productionStorage);
        AssertSuccess(await RunPowerShellAsync(
            "user-04-cleanup",
            new Dictionary<string, string> { ["STORAGE"] = productionStorage }));
        Assert.False(Directory.Exists(productionStorage));
    }

    private static IReadOnlyDictionary<string, DocumentationExample> Examples() =>
        DocumentationContractTests.ReadExamples()
            .ToDictionary(example => example.Id, StringComparer.Ordinal);

    private static DocumentationExample Example(string id) => Examples()[id];

    private static async Task<ProcessResult> RunPowerShellAsync(
        string id,
        IReadOnlyDictionary<string, string> replacements,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        using var script = TemporaryFile.Create(".ps1");
        await File.WriteAllTextAsync(script.Path, Substitute(Example(id).Content, replacements));
        var result = await RunAsync(
            "pwsh",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script.Path],
            workingDirectory ?? RepositoryRoot,
            timeout ?? TimeSpan.FromMinutes(2));
        return result with { Context = id };
    }

    private static string Substitute(
        string content,
        IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var (name, value) in replacements)
        {
            content = content.Replace($"{{{{{name}}}}}", value, StringComparison.Ordinal);
        }

        var unresolved = System.Text.RegularExpressions.Regex.Match(content, @"\{\{[A-Z_]+\}\}");
        Assert.False(unresolved.Success, $"Unresolved substitution {unresolved.Value}.");
        return content;
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
        return new ProcessResult(process.ExitCode, await output + await error);
    }

    private static void AssertSuccess(ProcessResult result) =>
        Assert.True(
            result.ExitCode == 0,
            $"{result.Context ?? "Command"} failed:{Environment.NewLine}{result.Output}");

    private static async Task<bool> IsListedAsync(NuGetTestServerHost server)
    {
        var packages = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/packages");
        return packages.EnumerateArray()
            .Single(package =>
                package.GetProperty("id").GetString() == "NuTest.Docs.Workflow" &&
                package.GetProperty("version").GetString() == "1.0.0")
            .GetProperty("listed")
            .GetBoolean();
    }

    private static string[] Lines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] ProductProjectEdges()
    {
        string[] projectPaths =
        [
            "src\\NuGet.TestServer.Extensions.Sdk\\NuGet.TestServer.Extensions.Sdk.csproj",
            "src\\NuGet.TestServer.Kernel\\NuGet.TestServer.Kernel.csproj",
            "src\\NuGet.TestServer.Extensions.Official\\NuGet.TestServer.Extensions.Official.csproj",
            "src\\NuGet.TestServer\\NuGet.TestServer.csproj",
            "src\\NuGet.TestServer.Cli\\NuGet.TestServer.Cli.csproj",
            "src\\NuGet.TestServer.Extensions.TestKit\\NuGet.TestServer.Extensions.TestKit.csproj",
            "src\\NuGet.TestServer.Extensions.PackageStaging\\NuGet.TestServer.Extensions.PackageStaging.csproj"
        ];
        var projects = projectPaths
            .Select(ResolveRepositoryPath)
            .ToDictionary(path => path, ProjectAssemblyName, StringComparer.OrdinalIgnoreCase);

        return projectPaths[1..]
            .Select(ResolveRepositoryPath)
            .SelectMany(path =>
            {
                var document = XDocument.Load(path);
                return document.Descendants("ProjectReference")
                    .Select(reference => reference.Attribute("Include")?.Value)
                    .Where(include => include is not null)
                    .Select(include => Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(path)!,
                        include!.Replace('\\', Path.DirectorySeparatorChar))))
                    .Where(projects.ContainsKey)
                    .Select(reference => $"{projects[path]} -> {projects[reference]}");
            })
            .ToArray();
    }

    private static string[] BrokerBackedPublicCapabilities()
    {
        var broker = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Kernel",
            "Kernel",
            "Capabilities",
            "CapabilityBroker.cs"));
        var exportedSdkTypes = typeof(NuGet.TestServer.Extensions.Sdk.IExtensionModule)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        return System.Text.RegularExpressions.Regex.Matches(
                broker,
                @"var type when type == typeof\((I[A-Za-z0-9]+Capability)\) =>")
            .Select(match => match.Groups[1].Value)
            .Where(exportedSdkTypes.Contains)
            .ToArray();
    }

    private static string[] PublicPackageVersions() =>
        new[]
        {
            "src\\NuGet.TestServer.Extensions.Sdk\\NuGet.TestServer.Extensions.Sdk.csproj",
            "src\\NuGet.TestServer.Extensions.TestKit\\NuGet.TestServer.Extensions.TestKit.csproj",
            "src\\NuGet.TestServer.Extensions.PackageStaging\\NuGet.TestServer.Extensions.PackageStaging.csproj"
        }
        .Select(relativePath => XDocument.Load(ResolveRepositoryPath(relativePath)))
        .Select(document =>
            $"{RequiredProperty(document, "PackageId")} {RequiredProperty(document, "Version")} " +
            RequiredProperty(document, "TargetFramework"))
        .ToArray();

    private static string ProjectAssemblyName(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document.Descendants("AssemblyName").Select(element => element.Value).FirstOrDefault()
            ?? Path.GetFileNameWithoutExtension(projectPath);
    }

    private static string RequiredProperty(XDocument document, string name) =>
        document.Descendants(name).Select(element => element.Value).FirstOrDefault()
        ?? throw new InvalidOperationException($"Project property '{name}' is required.");

    private static string ResolveRepositoryPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('\\', Path.DirectorySeparatorChar)));

    private static string EscapeXml(string value) =>
        System.Security.SecurityElement.Escape(value) ?? value;

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RepositoryRoot => DocumentationContractTests.RepositoryRoot;

    private sealed record ProcessResult(int ExitCode, string Output, string? Context = null);

    private sealed class TemporaryFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryFile Create(string extension)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"nutest-docs-{Guid.NewGuid():N}{extension}");
            return new TemporaryFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    private sealed class TemporaryDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryDirectory Create()
        {
            var parent = OperatingSystem.IsMacOS()
                ? System.IO.Path.Combine(RepositoryRoot, "artifacts", "documentation-tests")
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NuGet.TestServer.DocumentationTests");
            var path = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
