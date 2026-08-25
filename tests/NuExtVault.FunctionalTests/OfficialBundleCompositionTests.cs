using System.Collections.Immutable;
using System.Reflection;
using NuExtVault.Authentication;
using NuExtVault.Cli;
using NuExtVault.Hosting;
using NuExtVault.Packages;

namespace NuExtVault.FunctionalTests;

/// <summary>
/// Step 18 host-selection fitness. Every supported bootstrap path — standard, embedded,
/// production, the CLI, and the programmatic embedded host — must explicitly select the
/// official extension bundle, and the bundle must physically ship as its own assembly.
/// </summary>
public sealed class OfficialBundleCompositionTests
{
    private const string OfficialAssembly = "NuExtVault.Extensions.Official";

    private static readonly ImmutableArray<string> OfficialExtensionIds =
    [
        "NuExtVault.SupplyChain",
        "builtin.flat-container",
        "builtin.operations",
        "builtin.package-management",
        "builtin.registration",
        "builtin.search",
        "builtin.service-index",
        "builtin.test-control",
        "builtin.vulnerabilities"
    ];

    [Fact]
    public void The_cli_bootstrap_explicitly_selects_the_official_bundle()
    {
        using var storage = TemporaryDirectory.Create();
        var standard = CliServerProfileFactory.Create(
            production: false,
            "http://127.0.0.1:0",
            storage.Path,
            AuthenticationConfiguration.Create(null, null, "publish-key"),
            PackageTransferLimits.Default,
            trustedProxies: null);

        Assert.Equal(
            OfficialExtensionIds.Order(StringComparer.Ordinal),
            SelectedOfficialIds(standard.Profile.Extensions.Select(extension => extension.Id)));
        Assert.Equal(
            OfficialExtensionIds.Order(StringComparer.Ordinal),
            SelectedOfficialIds(
                standard.ExtensionGraph.Extensions.Select(extension => extension.Id)));
    }

    [Fact]
    public void The_production_cli_bootstrap_explicitly_selects_the_official_bundle()
    {
        using var storage = TemporaryDirectory.Create();
        var security = ProductionSecurityConfiguration.Create(
        [
            new("publisher", ["publish-key"], [SecurityScope.Publish], ["*"])
        ]);
        var production = CliServerProfileFactory.Create(
            production: true,
            "http://127.0.0.1:0",
            storage.Path,
            AuthenticationConfiguration.CreateProduction(security),
            PackageTransferLimits.Default,
            new TrustedProxyOptions(["127.0.0.1"]));

        var expected = OfficialExtensionIds
            .Where(id => id != "builtin.test-control")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expected,
            SelectedOfficialIds(production.Profile.Extensions.Select(extension => extension.Id)));
        Assert.Equal(
            expected,
            SelectedOfficialIds(
                production.ExtensionGraph.Extensions.Select(extension => extension.Id)));
    }

    [Fact]
    public async Task The_programmatic_embedded_host_runs_the_official_bundle()
    {
        await using var server = await NuExtVaultHost.StartAsync();

        Assert.Equal(
            OfficialExtensionIds.Order(StringComparer.Ordinal),
            SelectedOfficialIds(
                server.Composition.ExtensionGraph.Extensions.Select(extension => extension.Id)));

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .ToArray();
        Assert.Contains(OfficialAssembly, loaded);
    }

    [Fact]
    public async Task Parallel_embedded_hosts_stay_isolated_while_running_the_same_bundle()
    {
        await using var first = await NuExtVaultHost.StartAsync();
        await using var second = await NuExtVaultHost.StartAsync();

        Assert.NotSame(first.Composition, second.Composition);
        Assert.NotEqual(first.Composition.InstanceId, second.Composition.InstanceId);
        Assert.Equal(
            SelectedOfficialIds(
                first.Composition.ExtensionGraph.Extensions.Select(extension => extension.Id)),
            SelectedOfficialIds(
                second.Composition.ExtensionGraph.Extensions.Select(extension => extension.Id)));
        Assert.Equal(
            first.Composition.ExtensionGraph.Diagnostics,
            second.Composition.ExtensionGraph.Diagnostics);
    }

    [Fact]
    public void The_official_bundle_ships_next_to_the_cli_tool()
    {
        var cliDirectory = Path.GetDirectoryName(typeof(CliServerProfileFactory).Assembly.Location);
        Assert.NotNull(cliDirectory);
        Assert.True(
            File.Exists(Path.Combine(cliDirectory!, $"{OfficialAssembly}.dll")),
            $"'{OfficialAssembly}.dll' is not published beside the CLI tool.");
    }

    private static string[] SelectedOfficialIds(IEnumerable<string> ids)
    {
        var official = OfficialExtensionIds.ToHashSet(StringComparer.Ordinal);
        return ids.Where(official.Contains).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuExtVault.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
