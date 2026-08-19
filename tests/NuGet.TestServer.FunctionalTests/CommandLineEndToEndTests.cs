using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class CommandLineEndToEndTests
{
    [Fact]
    public async Task Dotnet_restore_uses_basic_credentials_for_a_private_source()
    {
        var authentication = AuthenticationConfiguration.Create(
            "test-user",
            "test-password",
            apiKey: null);
        await using var server = await NuGetTestServerHost.StartAsync(authentication);
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Private.Package", "1.0.0").Build());
        using var directory = TemporaryDirectory.Create();
        var projectPath = Path.Combine(directory.Path, "Private.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Private.Package" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        var configPath = Path.Combine(directory.Path, "NuGet.config");
        await File.WriteAllTextAsync(
            configPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="PrivateTestServer" value="{server.ServiceIndexUrl}" allowInsecureConnections="true" />
              </packageSources>
              <packageSourceCredentials>
                <PrivateTestServer>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="test-password" />
                  <add key="ValidAuthenticationTypes" value="basic" />
                </PrivateTestServer>
              </packageSourceCredentials>
            </configuration>
            """);

        var result = await RunAsync(
            "dotnet",
            $"restore \"{projectPath}\" --configfile \"{configPath}\" --no-cache",
            directory.Path);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains(
            "Private.Package/1.0.0",
            await File.ReadAllTextAsync(Path.Combine(directory.Path, "obj", "project.assets.json")));
    }

    [Fact]
    public async Task Dotnet_nuget_push_sends_the_configured_api_key()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        await using var server = await NuGetTestServerHost.StartAsync(authentication);
        using var directory = TemporaryDirectory.Create();
        var package = TestPackageBuilder.Create("Authenticated.Push", "1.0.0").Build();
        var packagePath = Path.Combine(directory.Path, "Authenticated.Push.1.0.0.nupkg");
        await File.WriteAllBytesAsync(packagePath, package.Content);
        var configPath = Path.Combine(directory.Path, "NuGet.config");
        await File.WriteAllTextAsync(
            configPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="TestServer" value="{server.ServiceIndexUrl}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);

        var result = await RunAsync(
            "dotnet",
            $"nuget push \"{packagePath}\" --source TestServer --api-key publish-key --configfile \"{configPath}\"",
            directory.Path);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.NotNull(await server.Packages.FindAsync("Authenticated.Push", "1.0.0"));
    }

    [Fact]
    public async Task Dotnet_restore_resolves_a_closed_dependency_graph()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Dependency.Package", "1.0.0").Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Root.Package", "2.0.0")
                .WithDependency("Dependency.Package", "[1.0.0]")
                .Build());

        using var directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "Test.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Root.Package" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        var configPath = Path.Combine(directory.Path, "NuGet.config");
        await File.WriteAllTextAsync(
            configPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="TestServer" value="{server.ServiceIndexUrl}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);

        var result = await RunAsync(
            "dotnet",
            $"restore \"{Path.Combine(directory.Path, "Test.csproj")}\" --configfile \"{configPath}\" --no-cache",
            directory.Path);

        Assert.True(result.ExitCode == 0, result.Output);
        var assets = await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "obj", "project.assets.json"));
        Assert.Contains("Root.Package/2.0.0", assets);
        Assert.Contains("Dependency.Package/1.0.0", assets);
    }

    [Fact]
    public async Task Dotnet_nuget_push_publishes_through_the_standard_client()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        using var directory = TemporaryDirectory.Create();
        var package = TestPackageBuilder.Create("Cli.Pushed.Package", "1.0.0").Build();
        var packagePath = Path.Combine(directory.Path, "Cli.Pushed.Package.1.0.0.nupkg");
        await File.WriteAllBytesAsync(packagePath, package.Content);
        var configPath = Path.Combine(directory.Path, "NuGet.config");
        await File.WriteAllTextAsync(
            configPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="TestServer" value="{server.ServiceIndexUrl}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);

        var result = await RunAsync(
            "dotnet",
            $"nuget push \"{packagePath}\" --source TestServer --api-key test --configfile \"{configPath}\"",
            directory.Path);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.NotNull(await server.Packages.FindAsync("Cli.Pushed.Package", "1.0.0"));
    }

    [Fact]
    public async Task Cli_start_exposes_a_healthy_server()
    {
        var port = GetAvailablePort();
        var cliPath = Path.Combine(AppContext.BaseDirectory, "NuGet.TestServer.Cli.dll");
        Assert.True(File.Exists(cliPath), $"CLI assembly not found at {cliPath}");
        using var storage = TemporaryDirectory.Create();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"\"{cliPath}\" start --port {port} --storage \"{storage.Path}\" " +
                "--max-request-bytes 1048576 --max-package-bytes 524288 " +
                "--max-archive-entries 100 --max-entry-bytes 262144 " +
                "--max-expanded-bytes 524288",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;

        try
        {
            using var client = new HttpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            HttpResponseMessage? response = null;
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    response = await client.GetAsync(
                        $"http://127.0.0.1:{port}/__test/health",
                        timeout.Token);
                    break;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100, timeout.Token);
                }
            }

            Assert.NotNull(response);
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    }

    [Fact]
    public async Task Cli_rejects_a_second_process_using_the_same_storage_root()
    {
        var firstPort = GetAvailablePort();
        var secondPort = GetAvailablePort();
        var cliPath = Path.Combine(AppContext.BaseDirectory, "NuGet.TestServer.Cli.dll");
        using var storage = TemporaryDirectory.Create();
        using var first = StartCli(cliPath, firstPort, storage.Path);

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{firstPort}")
            };
            await WaitUntilHealthyAsync(client);
            var second = await RunAsync(
                "dotnet",
                $"\"{cliPath}\" start --port {secondPort} --storage \"{storage.Path}\"",
                storage.Path);

            Assert.Equal(2, second.ExitCode);
            Assert.Contains("already in use", second.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Unhandled exception", second.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Cli_production_mode_rejects_anonymous_configuration()
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "NuGet.TestServer.Cli.dll");

        var result = await RunAsync(
            "dotnet",
            $"\"{cliPath}\" start --production",
            AppContext.BaseDirectory);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("authentication", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_production_mode_reports_mode_and_omits_control_api()
    {
        var port = GetAvailablePort();
        var cliPath = Path.Combine(AppContext.BaseDirectory, "NuGet.TestServer.Cli.dll");
        using var storage = TemporaryDirectory.Create();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"\"{cliPath}\" start --production --port {port} --storage \"{storage.Path}\" --api-key-env TEST_SERVER_KEY",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["TEST_SERVER_KEY"] = "publish-key";
        using var process = Process.Start(startInfo)!;

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
            await WaitUntilHealthyAsync(client);
            using var health = await client.GetAsync("/__test/health");
            var healthBody = await health.Content.ReadAsStringAsync();
            using var control = await client.GetAsync("/__test/state");

            Assert.Contains("\"mode\":\"production\"", healthBody);
            Assert.Equal(HttpStatusCode.NotFound, control.StatusCode);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var output = await process.StandardOutput.ReadToEndAsync() +
                         await process.StandardError.ReadToEndAsync();
            Assert.Contains("Mode:        Production", output);
            Assert.DoesNotContain("Control API:", output);
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
    public async Task Cli_api_key_option_protects_push_but_not_reads()
    {
        var port = GetAvailablePort();
        var cliPath = Path.Combine(AppContext.BaseDirectory, "NuGet.TestServer.Cli.dll");
        using var storage = TemporaryDirectory.Create();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"\"{cliPath}\" start --port {port} --storage \"{storage.Path}\" --api-key-env TEST_SERVER_KEY",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["TEST_SERVER_KEY"] = "publish-key";
        using var process = Process.Start(startInfo)!;

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
            await WaitUntilHealthyAsync(client);
            var package = TestPackageBuilder.Create("Cli.Authenticated", "1.0.0").Build();

            using var index = await client.GetAsync("/v3/index.json");
            using var anonymousPush = await client.PutAsync(
                "/package",
                new ByteArrayContent(package.Content));
            using var pushRequest = new HttpRequestMessage(HttpMethod.Put, "/package")
            {
                Content = new ByteArrayContent(package.Content)
            };
            pushRequest.Headers.Add("X-NuGet-ApiKey", "publish-key");
            using var authenticatedPush = await client.SendAsync(pushRequest);

            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousPush.StatusCode);
            Assert.Equal(HttpStatusCode.Created, authenticatedPush.StatusCode);
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

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static Process StartCli(string cliPath, int port, string storagePath) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{cliPath}\" start --port {port} --storage \"{storagePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilHealthyAsync(HttpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/__test/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            const int maxAttempts = 20;
            for (var attempt = 1; Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
