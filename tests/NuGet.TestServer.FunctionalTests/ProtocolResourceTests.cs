using System.Net;
using System.Text.Json;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class ProtocolResourceTests
{
    [Fact]
    public async Task NuGet_protocol_can_enumerate_and_download_a_seeded_package()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Example.Package", "1.0.0")
                .WithFile("lib/net10.0/example.txt", "content")
                .Build());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>()
            ?? throw new InvalidOperationException("PackageBaseAddress was not discovered.");
        using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
        var versions = await resource.GetAllVersionsAsync(
            "example.package", cache, NullLogger.Instance, CancellationToken.None);
        await using var destination = new MemoryStream();
        var copied = await resource.CopyNupkgToStreamAsync(
            "EXAMPLE.PACKAGE",
            NuGet.Versioning.NuGetVersion.Parse("1.0.0"),
            destination,
            cache,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["1.0.0"], versions.Select(v => v.ToNormalizedString()));
        Assert.True(copied);
        Assert.NotEmpty(destination.ToArray());
    }

    [Fact]
    public async Task Registration_and_search_are_projected_from_package_state()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.5.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "2.0.0-beta.1").Build());

        using var registration = await server.HttpClient.GetAsync("/registration/example.logging/index.json");
        registration.EnsureSuccessStatusCode();
        using var registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStreamAsync());
        var page = Assert.Single(
            registrationJson.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(3, page.GetProperty("count").GetInt32());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var search = await repository.GetResourceAsync<PackageSearchResource>()
            ?? throw new InvalidOperationException("SearchQueryService was not discovered.");
        var results = await search.SearchAsync(
            "logging",
            new SearchFilter(includePrerelease: false),
            skip: 0,
            take: 20,
            NullLogger.Instance,
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Example.Logging", result.Identity.Id);
        Assert.Equal("1.5.0", result.Identity.Version.ToNormalizedString());
        Assert.Equal(
            ["1.0.0", "1.5.0"],
            (await result.GetVersionsAsync()).Select(version => version.Version.ToNormalizedString()));
    }

    [Fact]
    public async Task Search_total_and_pages_are_stable_for_casing_offsets_and_empty_queries()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Zulu.Package", "1.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("alpha.Package", "1.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Bravo.Package", "1.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Hidden.Package", "1.0.0").Build());
        using var unlist = await server.HttpClient.DeleteAsync("/package/Hidden.Package/1.0.0");
        Assert.Equal(HttpStatusCode.NoContent, unlist.StatusCode);

        using var response = await server.HttpClient.GetAsync(
            "/query?q=PACKAGE&skip=1&take=1&prerelease=false");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(3, json.RootElement.GetProperty("totalHits").GetInt32());
        var item = Assert.Single(json.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal("Bravo.Package", item.GetProperty("id").GetString());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var search = await repository.GetResourceAsync<PackageSearchResource>()
            ?? throw new InvalidOperationException("SearchQueryService was not discovered.");
        var results = (await search.SearchAsync(
            string.Empty,
            new SearchFilter(includePrerelease: false),
            skip: 2,
            take: 2,
            NullLogger.Instance,
            CancellationToken.None)).ToArray();

        var result = Assert.Single(results);
        Assert.Equal("Zulu.Package", result.Identity.Id);
    }

    [Fact]
    public async Task Search_versions_follow_stable_and_prerelease_filters()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Versions.Package", "1.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Versions.Package", "2.0.0-beta.1").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Versions.Package", "2.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Versions.Package", "3.0.0-beta.1").Build());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var search = await repository.GetResourceAsync<PackageSearchResource>()
            ?? throw new InvalidOperationException("SearchQueryService was not discovered.");

        var stable = Assert.Single(await search.SearchAsync(
            "versions",
            new SearchFilter(includePrerelease: false),
            skip: 0,
            take: 20,
            NullLogger.Instance,
            CancellationToken.None));
        var prerelease = Assert.Single(await search.SearchAsync(
            "VERSIONS",
            new SearchFilter(includePrerelease: true),
            skip: 0,
            take: 20,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal("2.0.0", stable.Identity.Version.ToNormalizedString());
        Assert.Equal(
            ["1.0.0", "2.0.0"],
            (await stable.GetVersionsAsync()).Select(version => version.Version.ToNormalizedString()));
        Assert.Equal("3.0.0-beta.1", prerelease.Identity.Version.ToNormalizedString());
        Assert.Equal(
            ["1.0.0", "2.0.0-beta.1", "2.0.0", "3.0.0-beta.1"],
            (await prerelease.GetVersionsAsync()).Select(version => version.Version.ToNormalizedString()));
    }

    [Fact]
    public async Task Durable_indexed_queries_survive_restart_with_correct_protocol_metadata()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "NuGet.TestServer.FunctionalTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = await NuGetTestServerHost.StartAsync(
                             directory,
                             PackageTransferLimits.Default))
            {
                await writer.Packages.AddAsync(
                    TestPackageBuilder.Create("Durable.Search", "1.0.0").Build());
                await writer.Packages.AddAsync(
                    TestPackageBuilder.Create("Durable.Search", "2.0.0-beta.1").Build());
                await writer.Packages.AddAsync(
                    TestPackageBuilder.Create("Durable.Search", "2.0.0").Build());
            }

            await using var reader = await NuGetTestServerHost.StartAsync(
                directory,
                PackageTransferLimits.Default);
            var repository = Repository.Factory.GetCoreV3(reader.ServiceIndexUrl.ToString());
            var search = await repository.GetResourceAsync<PackageSearchResource>()
                ?? throw new InvalidOperationException("SearchQueryService was not discovered.");
            var result = Assert.Single(await search.SearchAsync(
                "durable",
                new SearchFilter(includePrerelease: false),
                0,
                20,
                NullLogger.Instance,
                CancellationToken.None));

            Assert.Equal("2.0.0", result.Identity.Version.ToNormalizedString());
            Assert.Equal(
                ["1.0.0", "2.0.0"],
                (await result.GetVersionsAsync()).Select(version => version.Version.ToNormalizedString()));

            using var response = await reader.HttpClient.GetAsync("/query?q=durable&skip=0&take=1");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            Assert.Equal(1, json.RootElement.GetProperty("totalHits").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Protocol_delete_unlists_without_removing_download_content()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example", "1.0.0").Build());

        using var delete = await server.HttpClient.DeleteAsync("/package/Example/1.0.0");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var package = await server.Packages.FindAsync("Example", "1.0.0");
        Assert.NotNull(package);
        Assert.False(package.IsListed);

        using var download = await server.HttpClient.GetAsync(
            "/flatcontainer/example/1.0.0/example.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }
}
