using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NuExtVault.Extensions.Sdk;
using NuExtVault.ExternalExtensionTestKit;
using NuExtVault.Packages;

namespace NuExtVault.FunctionalTests;

/// <summary>
/// Step 22 command-line coverage: an administrator installs the optional Package
/// Staging extension into the real CLI with <c>--extension-root</c>,
/// <c>--extension-trust-root</c>, and explicit <c>--extension-grant</c> capability
/// grants, then drives the staging workflow over HTTP. Capabilities stay denied by
/// default, so the same install without grants must fail startup.
/// </summary>
[Collection(nameof(PackageStagingFunctionalAssetsCollection))]
public sealed class PackageStagingCliTests(PackageStagingFunctionalAssetsFixture fixture)
{
    private static readonly string[] Grants =
    [
        BuiltInCapabilityNames.HostClockRead,
        BuiltInCapabilityNames.ExtensionStateRead,
        BuiltInCapabilityNames.ExtensionStateWrite,
        BuiltInCapabilityNames.PackageContentWriteStaged,
        BuiltInCapabilityNames.PublicationRequest
    ];

    [Fact]
    public async Task An_installed_staging_extension_without_grants_fails_startup()
    {
        using var install = Install(fixture.StagingAssets);
        var port = GetAvailablePort();
        using var cli = StartCli(install, port, grants: []);

        var exited = cli.WaitForExit(TimeSpan.FromSeconds(60));
        var error = await cli.StandardError.ReadToEndAsync();

        Assert.True(exited, "The CLI should fail closed instead of listening.");
        Assert.NotEqual(0, cli.ExitCode);
        Assert.Contains("catalog.missing-capability-grant", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_can_install_start_stage_and_promote_through_the_cli()
    {
        using var install = Install(fixture.StagingAssets);
        var port = GetAvailablePort();
        using var cli = StartCli(install, port, Grants);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
            await WaitUntilHealthyAsync(client);

            using var create = await client.PutAsync(
                "/staging/groups/cli",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            var content = Nupkg("Contoso.Cli", "4.5.6");
            using var upload = await client.PutAsync(
                "/staging/groups/cli/packages",
                new ByteArrayContent(content));
            using var beforePromotion = await client.GetAsync(
                "/flatcontainer/contoso.cli/index.json");
            using var promote = await client.PostAsync(
                "/staging/groups/cli/packages/Contoso.Cli/4.5.6/promote",
                null);
            using var afterPromotion = await client.GetAsync(
                "/flatcontainer/contoso.cli/index.json");

            Assert.Equal("Succeeded", await OutcomeAsync(create));
            Assert.Equal("Succeeded", await OutcomeAsync(upload));
            Assert.Equal(HttpStatusCode.NotFound, beforePromotion.StatusCode);
            Assert.Equal("Succeeded", await OutcomeAsync(promote));
            Assert.Equal(HttpStatusCode.OK, afterPromotion.StatusCode);
            using var versions = JsonDocument.Parse(
                await afterPromotion.Content.ReadAsStringAsync());
            Assert.Equal(
                ["4.5.6"],
                versions.RootElement.GetProperty("versions").EnumerateArray()
                    .Select(entry => entry.GetString() ?? string.Empty)
                    .ToArray());
        }
        finally
        {
            if (!cli.HasExited)
            {
                cli.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<string> OutcomeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            !string.IsNullOrWhiteSpace(body),
            $"Expected a JSON body but got {(int)response.StatusCode} with no payload.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("outcome").GetString() ?? string.Empty;
    }

    private static byte[] Nupkg(string id, string version)
    {
        using var package = TestPackageBuilder.Create(id, version).Build();
        return package.Content;
    }

    private static StagingInstall Install(ContosoFlavorsAssets assets)
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(publisher: "NuExtVault");
        var install = StagingInstall.Create();
        File.WriteAllBytes(
            Path.Combine(install.ExtensionRoot, "NuExtVault.PackageStaging.nupkg"),
            ExternalExtensionPackageBuilder.BuildValidPackage(assets, key));
        File.WriteAllText(
            install.TrustRootPath,
            JsonSerializer.Serialize(new
            {
                publisher = trustRoot.Publisher,
                keyId = trustRoot.KeyId,
                algorithm = trustRoot.Algorithm,
                subjectPublicKeyInfoBase64 =
                    Convert.ToBase64String(trustRoot.SubjectPublicKeyInfo.ToArray())
            }));
        return install;
    }

    private static Process StartCli(StagingInstall install, int port, string[] grants)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "NuExtVault.Cli.dll");
        Assert.True(File.Exists(cliPath), $"CLI assembly not found at {cliPath}");
        var arguments = new StringBuilder()
            .Append('"').Append(cliPath).Append('"')
            .Append(" start --port ").Append(port)
            .Append(" --storage \"").Append(install.StorageRoot).Append('"')
            .Append(" --extension-root \"").Append(install.ExtensionRoot).Append('"')
            .Append(" --extension-trust-root \"").Append(install.TrustRootPath).Append('"');
        foreach (var grant in grants)
        {
            arguments.Append(" --extension-grant ").Append(grant);
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
    }

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/health/live", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener is not ready yet.
            }

            await Task.Delay(200, timeout.Token);
        }
    }

    private sealed class StagingInstall : IDisposable
    {
        private StagingInstall(string root)
        {
            Root = root;
            ExtensionRoot = Path.Combine(root, "extensions");
            StorageRoot = Path.Combine(root, "storage");
            TrustRootPath = Path.Combine(root, "trust-root.json");
            Directory.CreateDirectory(ExtensionRoot);
            Directory.CreateDirectory(StorageRoot);
        }

        public string Root { get; }

        public string ExtensionRoot { get; }

        public string StorageRoot { get; }

        public string TrustRootPath { get; }

        public static StagingInstall Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                "nuextvault-staging-cli",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A locked storage directory is cleaned up by the test host.
            }
        }
    }
}
