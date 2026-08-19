using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    [Fact]
    public async Task Flat_container_serves_the_exact_package_sha512_for_get_and_head()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Example.Hash", "1.0.0").Build();
        await server.Packages.AddAsync(package);
        const string path = "/flatcontainer/example.hash/1.0.0/example.hash.1.0.0.nupkg.sha512";

        using var response = await server.HttpClient.GetAsync(path);
        using var head = await server.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            Convert.ToBase64String(SHA512.HashData(package.Content)),
            await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Registration_and_search_project_rich_package_metadata()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Example.Metadata", "1.0.0")
                .WithAuthors("Alice, Bob")
                .WithDescription("Description")
                .WithSummary("Summary")
                .WithTitle("Title")
                .WithTags("one two")
                .WithProjectUrl("https://example.test/project")
                .WithReadme("README.md", "# Read me")
                .WithIcon("icon.png", [1, 2, 3])
                .WithLicenseExpression("MIT")
                .WithPackageType("DotnetTool", "1.0.0")
                .WithRepository("git", "https://example.test/repository.git", "abc123", "main")
                .Build());

        using var registration = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/registration/example.metadata/1.0.0.json"));
        var catalog = registration.RootElement.GetProperty("catalogEntry");
        Assert.Equal("Summary", catalog.GetProperty("summary").GetString());
        Assert.Equal("Title", catalog.GetProperty("title").GetString());
        Assert.Equal("https://example.test/project", catalog.GetProperty("projectUrl").GetString());
        Assert.Equal("README.md", catalog.GetProperty("readme").GetString());
        Assert.Equal("icon.png", catalog.GetProperty("icon").GetString());
        Assert.Equal("MIT", catalog.GetProperty("licenseExpression").GetString());
        Assert.Equal("DotnetTool", catalog.GetProperty("packageTypes")[0].GetProperty("name").GetString());
        Assert.Equal("abc123", catalog.GetProperty("repository").GetProperty("commit").GetString());

        using var search = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/query?q=metadata"));
        var result = Assert.Single(search.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal("Summary", result.GetProperty("summary").GetString());
        Assert.Equal("https://example.test/project", result.GetProperty("projectUrl").GetString());
        Assert.Equal("DotnetTool", result.GetProperty("packageTypes")[0].GetProperty("name").GetString());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>()
            ?? throw new InvalidOperationException("RegistrationsBaseUrl was not discovered.");
        using var cache = new SourceCacheContext { NoCache = true };
        var metadata = Assert.Single(await metadataResource.GetMetadataAsync(
            "Example.Metadata",
            includePrerelease: false,
            includeUnlisted: false,
            cache,
            NullLogger.Instance,
            CancellationToken.None));
        Assert.Equal(new Uri("https://example.test/project"), metadata.ProjectUrl);
    }

    [Fact]
    public async Task Search_35_filters_package_types_and_emits_implicit_dependency()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Library", "1.0.0").Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Tool", "1.0.0")
                .WithPackageType("DotnetTool", "1.0.0")
                .Build());

        using var dependencySearch = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/query?packageType=Dependency"));
        using var toolSearch = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/query?packageType=dotnettool"));

        var dependency = Assert.Single(
            dependencySearch.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal("Library", dependency.GetProperty("id").GetString());
        Assert.Equal(
            "Dependency",
            dependency.GetProperty("packageTypes")[0].GetProperty("name").GetString());
        var tool = Assert.Single(
            toolSearch.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal("Tool", tool.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Control_metadata_projects_owners_downloads_verification_and_deprecation()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Legacy.Package", "1.0.0")
                .WithAuthors("Original Author")
                .Build());
        using var update = await server.HttpClient.PutAsJsonAsync(
            "/__test/packages/Legacy.Package/1.0.0/metadata",
            new
            {
                owners = new[] { "Alice", "Bob" },
                downloads = 42,
                verified = true,
                deprecation = new
                {
                    reasons = new[] { "Legacy", "Other" },
                    message = "Use the replacement.",
                    alternatePackage = new
                    {
                        id = "Replacement.Package",
                        range = "[2.0.0,)"
                    }
                }
            });
        update.EnsureSuccessStatusCode();

        using var registration = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/registration/legacy.package/1.0.0.json"));
        var catalog = registration.RootElement.GetProperty("catalogEntry");
        Assert.Equal(["Alice", "Bob"], catalog.GetProperty("owners")
            .EnumerateArray().Select(owner => owner.GetString()!).ToArray());
        Assert.Equal(42, catalog.GetProperty("downloads").GetInt64());
        Assert.Equal("Use the replacement.",
            catalog.GetProperty("deprecation").GetProperty("message").GetString());
        Assert.Equal("Replacement.Package",
            catalog.GetProperty("deprecation").GetProperty("alternatePackage")
                .GetProperty("id").GetString());

        using var search = JsonDocument.Parse(
            await server.HttpClient.GetStreamAsync("/query?q=legacy"));
        var result = Assert.Single(search.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal(42, result.GetProperty("totalDownloads").GetInt64());
        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal(42, result.GetProperty("versions")[0].GetProperty("downloads").GetInt64());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>()
            ?? throw new InvalidOperationException("RegistrationsBaseUrl was not discovered.");
        using var cache = new SourceCacheContext { NoCache = true };
        var metadata = Assert.Single(await metadataResource.GetMetadataAsync(
            "Legacy.Package",
            includePrerelease: false,
            includeUnlisted: false,
            cache,
            NullLogger.Instance,
            CancellationToken.None));
        Assert.Equal("Alice, Bob", metadata.Owners);
        var deprecation = await metadata.GetDeprecationMetadataAsync();
        Assert.Equal("Use the replacement.", deprecation!.Message);
    }

    [Fact]
    public async Task Control_metadata_rejects_null_collections()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example", "1.0.0").Build());
        using var content = JsonContent.Create(new
        {
            owners = (string[]?)null,
            downloads = 0,
            verified = false,
            deprecation = (object?)null
        });

        using var response = await server.HttpClient.PutAsync(
            "/__test/packages/Example/1.0.0/metadata",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
