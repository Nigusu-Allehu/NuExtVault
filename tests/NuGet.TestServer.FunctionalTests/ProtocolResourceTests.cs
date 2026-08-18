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
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "2.0.0-beta.1").Build());

        using var registration = await server.HttpClient.GetAsync("/registration/example.logging/index.json");
        registration.EnsureSuccessStatusCode();
        using var registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStreamAsync());
        var page = Assert.Single(
            registrationJson.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(2, page.GetProperty("count").GetInt32());

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
        Assert.Equal("1.0.0", result.Identity.Version.ToNormalizedString());
    }

    [Fact]
    public async Task Advertised_registration_page_returns_bounds_parent_and_leaves()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "2.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.0").Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Example.Logging", "2.0.0-beta.1").Build());

        using var index = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/registration/EXAMPLE.LOGGING/index.json"));
        var advertisedPage = Assert.Single(
            index.RootElement.GetProperty("items").EnumerateArray().ToArray());
        var pageUrl = advertisedPage.GetProperty("@id").GetString();

        using var response = await server.HttpClient.GetAsync(pageUrl);
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(pageUrl, page.RootElement.GetProperty("@id").GetString());
        Assert.Equal(
            index.RootElement.GetProperty("@id").GetString(),
            page.RootElement.GetProperty("parent").GetString());
        Assert.Equal("1.0.0", page.RootElement.GetProperty("lower").GetString());
        Assert.Equal("2.0.0", page.RootElement.GetProperty("upper").GetString());
        Assert.Equal(3, page.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(
            ["1.0.0", "2.0.0-beta.1", "2.0.0"],
            page.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("catalogEntry").GetProperty("version").GetString()!)
                .ToArray());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>()
            ?? throw new InvalidOperationException("RegistrationsBaseUrl was not discovered.");
        using var cache = new SourceCacheContext { NoCache = true };
        var metadata = await metadataResource.GetMetadataAsync(
            "EXAMPLE.LOGGING",
            includePrerelease: true,
            includeUnlisted: true,
            cache,
            NullLogger.Instance,
            CancellationToken.None);
        Assert.Equal(
            ["1.0.0", "2.0.0-beta.1", "2.0.0"],
            metadata.Select(item => item.Identity.Version.ToNormalizedString()).ToArray());
    }

    [Theory]
    [InlineData("/registration/missing/page/1.0.0/2.0.0.json")]
    [InlineData("/registration/example.logging/page/not-a-version/2.0.0.json")]
    [InlineData("/registration/example.logging/page/1.0.0/1.0.0.json")]
    public async Task Registration_page_returns_not_found_for_unknown_packages_or_invalid_bounds(
        string path)
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.0.0").Build());
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "2.0.0").Build());

        using var response = await server.HttpClient.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Registration_page_supports_head_without_a_response_body()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.0.0").Build());

        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            "/registration/EXAMPLE.LOGGING/page/1.0/1.0.0.json");
        using var response = await server.HttpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
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
