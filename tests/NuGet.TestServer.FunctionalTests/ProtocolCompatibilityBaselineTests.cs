using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.FunctionalTests;

public sealed class ProtocolCompatibilityBaselineTests
{
    [Fact]
    public async Task Service_index_contract_preserves_resource_order_casing_headers_and_head()
    {
        await using var server = await NuGetTestServerHost.StartAsync(CreateVulnerabilitySnapshot());

        using var get = await server.HttpClient.GetAsync("/v3/index.json");
        using var head = await SendHeadAsync(server, "/v3/index.json");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("application/json", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", get.Content.Headers.ContentType?.CharSet);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("application/json", head.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        var root = server.BaseUrl.GetLeftPart(UriPartial.Authority);
        var normalized = (await get.Content.ReadAsStringAsync()).Replace(root, "<ROOT>");
        Assert.Equal(
            """
            {"version":"3.0.0","resources":[{"@id":"<ROOT>/flatcontainer/","@type":"PackageBaseAddress/3.0.0"},{"@id":"<ROOT>/registration/","@type":"RegistrationsBaseUrl/3.6.0"},{"@id":"<ROOT>/query","@type":"SearchQueryService/3.0.0-beta"},{"@id":"<ROOT>/query","@type":"SearchQueryService/3.5.0"},{"@id":"<ROOT>/package","@type":"PackagePublish/2.0.0"},{"@id":"<ROOT>/symbolpackage","@type":"SymbolPackagePublish/4.9.0"},{"@id":"<ROOT>/v3/vulnerabilities/index.json","@type":"VulnerabilityInfo/6.7.0"}]}
            """,
            normalized);
    }

    [Fact]
    public async Task Flat_container_contract_preserves_versions_content_ranges_hash_and_head()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Baseline.Package", "2.0")
            .WithFile("lib/net10.0/payload.txt", "baseline")
            .Build();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Baseline.Package", "2.0.0-beta.1").Build());
        await server.Packages.AddAsync(package);
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Baseline.Package", "1.0").Build());

        using var versions = await server.HttpClient.GetAsync(
            "/flatcontainer/BASELINE.PACKAGE/index.json");
        using var versionsHead = await SendHeadAsync(
            server,
            "/flatcontainer/baseline.package/index.json");
        const string contentPath =
            "/flatcontainer/BASELINE.PACKAGE/2.0/baseline.package.2.0.0.nupkg";
        using var content = await server.HttpClient.GetAsync(contentPath);
        using var contentHead = await SendHeadAsync(server, contentPath);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, contentPath);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 7);
        using var range = await server.HttpClient.SendAsync(rangeRequest);
        using var nuspec = await server.HttpClient.GetAsync(
            "/flatcontainer/baseline.package/2.0.0/BASELINE.PACKAGE.nuspec");
        using var hash = await server.HttpClient.GetAsync(
            "/flatcontainer/baseline.package/2.0.0/baseline.package.2.0.0.nupkg.sha512");

        Assert.Equal(
            """{"versions":["1.0.0","2.0.0-beta.1","2.0.0"]}""",
            await versions.Content.ReadAsStringAsync());
        Assert.Equal("application/json", versions.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, versionsHead.StatusCode);
        Assert.Empty(await versionsHead.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("application/octet-stream", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal(package.Content.Length, content.Content.Headers.ContentLength);
        Assert.Contains("bytes", content.Headers.AcceptRanges);
        Assert.Equal(package.Content, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, contentHead.StatusCode);
        Assert.Equal(package.Content.Length, contentHead.Content.Headers.ContentLength);
        Assert.Empty(await contentHead.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(0, 7, package.Content.Length), range.Content.Headers.ContentRange);
        Assert.Equal(package.Content[..8], await range.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/xml", nuspec.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "<id>Baseline.Package</id>",
            await nuspec.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal("text/plain", hash.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            Convert.ToBase64String(SHA512.HashData(package.Content)),
            await hash.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Registration_contract_preserves_index_page_leaf_shape_order_and_casing()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Baseline.Registration", "2.0.0")
                .WithAuthors("Alice")
                .WithDescription("Stable")
                .Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Baseline.Registration", "1.0")
                .WithAuthors("Bob")
                .WithDescription("First")
                .Build());

        using var indexResponse = await server.HttpClient.GetAsync(
            "/registration/BASELINE.REGISTRATION/index.json");
        using var indexHead = await SendHeadAsync(
            server,
            "/registration/baseline.registration/index.json");
        using var index = JsonDocument.Parse(await indexResponse.Content.ReadAsStreamAsync());
        var indexRoot = index.RootElement;
        var pageReference = Assert.Single(indexRoot.GetProperty("items").EnumerateArray().ToArray());
        var pageUrl = pageReference.GetProperty("@id").GetString()!;

        using var pageResponse = await server.HttpClient.GetAsync(pageUrl);
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStreamAsync());
        var pageRoot = page.RootElement;
        using var leafResponse = await server.HttpClient.GetAsync(
            "/registration/baseline.registration/1.0.json");
        using var leafHead = await SendHeadAsync(
            server,
            "/registration/BASELINE.REGISTRATION/1.0.0.json");
        using var leaf = JsonDocument.Parse(await leafResponse.Content.ReadAsStreamAsync());
        var leafRoot = leaf.RootElement;
        var catalog = leafRoot.GetProperty("catalogEntry");

        Assert.Equal(["@id", "count", "items"], PropertyNames(indexRoot));
        Assert.EndsWith(
            "/registration/baseline.registration/index.json",
            indexRoot.GetProperty("@id").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(1, indexRoot.GetProperty("count").GetInt32());
        Assert.Equal(
            ["@id", "@type", "parent", "count", "lower", "upper", "items"],
            PropertyNames(pageRoot));
        Assert.Equal("catalog:CatalogPage", pageRoot.GetProperty("@type").GetString());
        Assert.Equal("1.0.0", pageRoot.GetProperty("lower").GetString());
        Assert.Equal("2.0.0", pageRoot.GetProperty("upper").GetString());
        Assert.Equal(
            ["1.0.0", "2.0.0"],
            pageRoot.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("catalogEntry").GetProperty("version").GetString()!)
                .ToArray());
        Assert.Equal(
            ["@id", "@type", "catalogEntry", "packageContent", "registration"],
            PropertyNames(leafRoot));
        Assert.Equal(
            [
                "@id", "@type", "id", "version", "authors", "owners", "downloads",
                "description", "summary", "title", "tags", "projectUrl", "readme", "icon",
                "licenseExpression", "licenseFile", "licenseUrl", "packageTypes", "repository",
                "listed", "published", "dependencyGroups"
            ],
            PropertyNames(catalog));
        Assert.Equal("Baseline.Registration", catalog.GetProperty("id").GetString());
        Assert.Equal("1.0.0", catalog.GetProperty("version").GetString());
        Assert.True(catalog.GetProperty("listed").GetBoolean());
        Assert.EndsWith(
            "/flatcontainer/baseline.registration/1.0.0/baseline.registration.1.0.0.nupkg",
            leafRoot.GetProperty("packageContent").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("application/json", indexResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", pageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", leafResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, indexHead.StatusCode);
        Assert.Empty(await indexHead.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, leafHead.StatusCode);
        Assert.Empty(await leafHead.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Search_contract_preserves_order_paging_prerelease_and_head()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("zulu.Baseline", "1.0.0").Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Alpha.Baseline", "2.0.0-beta.1").Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Alpha.Baseline", "1.0.0").Build());
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("bravo.Baseline", "1.0.0").Build());

        using var stableResponse = await server.HttpClient.GetAsync(
            "/query?q=BASELINE&skip=0&take=2&prerelease=false");
        using var prereleaseResponse = await server.HttpClient.GetAsync(
            "/query?q=baseline&skip=0&take=1&prerelease=true");
        using var head = await SendHeadAsync(
            server,
            "/query?q=baseline&skip=1&take=1&prerelease=false");
        using var stable = JsonDocument.Parse(await stableResponse.Content.ReadAsStreamAsync());
        using var prerelease = JsonDocument.Parse(
            await prereleaseResponse.Content.ReadAsStreamAsync());

        Assert.Equal(["totalHits", "data"], PropertyNames(stable.RootElement));
        Assert.Equal(3, stable.RootElement.GetProperty("totalHits").GetInt32());
        var results = stable.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(["Alpha.Baseline", "bravo.Baseline"], results
            .Select(result => result.GetProperty("id").GetString()!)
            .ToArray());
        Assert.Equal(
            [
                "@id", "@type", "registration", "id", "version", "description", "summary",
                "title", "tags", "authors", "owners", "projectUrl", "totalDownloads",
                "verified", "packageTypes", "versions"
            ],
            PropertyNames(results[0]));
        Assert.Equal("1.0.0", results[0].GetProperty("version").GetString());
        Assert.Equal(
            ["1.0.0"],
            results[0].GetProperty("versions")
                .EnumerateArray()
                .Select(item => item.GetProperty("version").GetString()!)
                .ToArray());
        var prereleaseResult = Assert.Single(
            prerelease.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal("2.0.0-beta.1", prereleaseResult.GetProperty("version").GetString());
        Assert.Equal(
            ["1.0.0", "2.0.0-beta.1"],
            prereleaseResult.GetProperty("versions")
                .EnumerateArray()
                .Select(item => item.GetProperty("version").GetString()!)
                .ToArray());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("application/json", head.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Vulnerability_contract_preserves_local_shape_timestamp_page_and_head()
    {
        var snapshot = CreateVulnerabilitySnapshot();
        await using var server = await NuGetTestServerHost.StartAsync(snapshot);

        using var indexResponse = await server.HttpClient.GetAsync(
            "/v3/vulnerabilities/index.json");
        using var indexHead = await SendHeadAsync(
            server,
            "/v3/vulnerabilities/index.json");
        using var index = JsonDocument.Parse(await indexResponse.Content.ReadAsStreamAsync());
        var entry = Assert.Single(index.RootElement.EnumerateArray().ToArray());
        var pageUrl = entry.GetProperty("@id").GetString()!;
        using var pageResponse = await server.HttpClient.GetAsync(pageUrl);
        using var pageHead = await SendHeadAsync(server, pageUrl);
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStreamAsync());
        var advisory = Assert.Single(
            page.RootElement.GetProperty("baseline.package").EnumerateArray().ToArray());

        Assert.Equal(["@name", "@id", "@updated", "comment"], PropertyNames(entry));
        Assert.Equal("base", entry.GetProperty("@name").GetString());
        Assert.Equal("2026-08-18T12:00:00.0000000+00:00", entry.GetProperty("@updated").GetString());
        Assert.Equal("frozen baseline", entry.GetProperty("comment").GetString());
        Assert.Equal(
            $"{server.BaseUrl.GetLeftPart(UriPartial.Authority)}/v3/vulnerabilities/{snapshot.Id}/base.json",
            pageUrl);
        Assert.Equal(["url", "severity", "versions"], PropertyNames(advisory));
        Assert.Equal("https://github.com/advisories/GHSA-baseline", advisory.GetProperty("url").GetString());
        Assert.Equal(2, advisory.GetProperty("severity").GetInt32());
        Assert.Equal("[1.0.0, 2.0.0)", advisory.GetProperty("versions").GetString());
        Assert.Equal("application/json", indexResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", pageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, indexHead.StatusCode);
        Assert.Empty(await indexHead.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, pageHead.StatusCode);
        Assert.Empty(await pageHead.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Push_unlist_quarantine_and_delete_are_immediately_visible_to_all_reads()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Immediate.Visibility", "1.0.0").Build();

        using var push = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));

        Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        Assert.Equal("/package", push.Headers.Location?.OriginalString);
        Assert.Equal("application/json", push.Content.Headers.ContentType?.MediaType);
        using (var pushJson = JsonDocument.Parse(await push.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(["outcome", "message"], PropertyNames(pushJson.RootElement));
            Assert.Equal(0, pushJson.RootElement.GetProperty("outcome").GetInt32());
            Assert.Equal("Package published.", pushJson.RootElement.GetProperty("message").GetString());
        }

        await AssertVisibilityAsync(
            server,
            "Immediate.Visibility",
            expectedVisible: true,
            expectedListed: true);
        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var find = await repository.GetResourceAsync<FindPackageByIdResource>()
            ?? throw new InvalidOperationException("PackageBaseAddress was not discovered.");
        using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
        Assert.Equal(
            ["1.0.0"],
            (await find.GetAllVersionsAsync(
                "IMMEDIATE.VISIBILITY",
                cache,
                NullLogger.Instance,
                CancellationToken.None)).Select(version => version.ToNormalizedString()));

        using var unlist = await server.HttpClient.DeleteAsync(
            "/package/Immediate.Visibility/1.0");
        Assert.Equal(HttpStatusCode.NoContent, unlist.StatusCode);
        await AssertVisibilityAsync(
            server,
            "Immediate.Visibility",
            expectedVisible: true,
            expectedListed: false);

        using var relist = await server.HttpClient.PostAsync(
            "/__test/packages/Immediate.Visibility/1.0.0/list",
            null);
        Assert.Equal(HttpStatusCode.NoContent, relist.StatusCode);
        using var quarantine = await server.HttpClient.PostAsync(
            "/__admin/packages/Immediate.Visibility/1.0.0/quarantine?reason=baseline",
            null);
        Assert.Equal(HttpStatusCode.NoContent, quarantine.StatusCode);
        await AssertVisibilityAsync(
            server,
            "Immediate.Visibility",
            expectedVisible: false,
            expectedListed: false);

        var deletePackage = TestPackageBuilder.Create("Immediate.Delete", "1.0.0").Build();
        using var deletePush = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(deletePackage.Content));
        Assert.Equal(HttpStatusCode.Created, deletePush.StatusCode);
        await AssertVisibilityAsync(
            server,
            "Immediate.Delete",
            expectedVisible: true,
            expectedListed: true);
        using var delete = await server.HttpClient.PostAsync(
            "/__admin/packages/Immediate.Delete/1.0.0/delete?reason=baseline",
            null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await AssertVisibilityAsync(
            server,
            "Immediate.Delete",
            expectedVisible: false,
            expectedListed: false);
        using var audit = await server.HttpClient.GetAsync("/__admin/supply-chain/audit");
        audit.EnsureSuccessStatusCode();
        using var auditJson = JsonDocument.Parse(await audit.Content.ReadAsStreamAsync());
        var deleteAudit = auditJson.RootElement.EnumerateArray()
            .Last(item =>
                item.GetProperty("packageId").GetString() == "Immediate.Delete" &&
                item.GetProperty("action").GetString() == "delete");
        Assert.Equal("deleted", deleteAudit.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Control_error_contract_preserves_status_content_type_shape_and_messages()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var malformed = await server.HttpClient.PostAsync(
            "/__test/packages",
            new StringContent("{", Encoding.UTF8, "application/json"));
        using var missing = await server.HttpClient.DeleteAsync(
            "/__test/packages/Missing.Package/1.0.0");
        using var missingReason = await server.HttpClient.PostAsync(
            "/__admin/packages/Missing.Package/1.0.0/quarantine",
            null);
        using var unknownAction = await server.HttpClient.PostAsync(
            "/__admin/packages/Missing.Package/1.0.0/unknown?reason=baseline",
            null);

        await AssertProblemAsync(
            malformed,
            HttpStatusCode.BadRequest,
            "The package request must contain valid JSON and base64 content.");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(await missing.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        Assert.Equal("application/json", missingReason.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "A moderation reason is required.",
            await missingReason.Content.ReadFromJsonAsync<string>());
        Assert.Equal(HttpStatusCode.BadRequest, unknownAction.StatusCode);
        Assert.Equal("application/json", unknownAction.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "Moderation action must be approve, reject, quarantine, or delete.",
            await unknownAction.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task Parallel_programmatic_hosts_are_port_state_and_request_isolated()
    {
        var hosts = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => NuGetTestServerHost.StartAsync()));
        try
        {
            Assert.Equal(hosts.Length, hosts.Select(host => host.Port).Distinct().Count());

            await Task.WhenAll(hosts.Select(
                (host, index) => host.Packages.AddAsync(
                    TestPackageBuilder.Create($"Isolated.Host.{index}", "1.0.0").Build()).AsTask()));
            await Task.WhenAll(hosts.Select(
                (host, index) => host.HttpClient.GetAsync($"/isolation-marker/{index}")));

            for (var index = 0; index < hosts.Length; index++)
            {
                using var own = await hosts[index].HttpClient.GetAsync(
                    $"/query?q=Isolated.Host.{index}");
                using var foreign = await hosts[index].HttpClient.GetAsync(
                    $"/query?q=Isolated.Host.{(index + 1) % hosts.Length}");
                using var ownJson = JsonDocument.Parse(await own.Content.ReadAsStreamAsync());
                using var foreignJson = JsonDocument.Parse(await foreign.Content.ReadAsStreamAsync());

                Assert.Equal(1, ownJson.RootElement.GetProperty("totalHits").GetInt32());
                Assert.Equal(0, foreignJson.RootElement.GetProperty("totalHits").GetInt32());
                var recordedPaths = (await hosts[index].Requests.GetAsync())
                    .Select(request => request.Path)
                    .ToArray();
                Assert.Contains($"/isolation-marker/{index}", recordedPaths);
                Assert.DoesNotContain(
                    $"/isolation-marker/{(index + 1) % hosts.Length}",
                    recordedPaths);
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    private static async Task AssertVisibilityAsync(
        NuGetTestServerHost server,
        string id,
        bool expectedVisible,
        bool expectedListed)
    {
        var normalizedId = id.ToLowerInvariant();
        using var versions = await server.HttpClient.GetAsync(
            $"/flatcontainer/{normalizedId}/index.json");
        using var content = await server.HttpClient.GetAsync(
            $"/flatcontainer/{normalizedId}/1.0.0/{normalizedId}.1.0.0.nupkg");
        using var registration = await server.HttpClient.GetAsync(
            $"/registration/{normalizedId}/1.0.0.json");
        using var search = await server.HttpClient.GetAsync(
            $"/query?q={Uri.EscapeDataString(id)}");
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStreamAsync());

        Assert.Equal(
            expectedVisible ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            versions.StatusCode);
        Assert.Equal(
            expectedVisible ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            content.StatusCode);
        Assert.Equal(
            expectedVisible ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            registration.StatusCode);
        Assert.Equal(expectedVisible && expectedListed ? 1 : 0,
            searchJson.RootElement.GetProperty("totalHits").GetInt32());

        if (expectedVisible)
        {
            using var registrationJson = JsonDocument.Parse(
                await registration.Content.ReadAsStreamAsync());
            Assert.Equal(
                expectedListed,
                registrationJson.RootElement
                    .GetProperty("catalogEntry")
                    .GetProperty("listed")
                    .GetBoolean());
        }
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(["type", "title", "status", "detail"], PropertyNames(problem.RootElement));
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
    }

    private static Task<HttpResponseMessage> SendHeadAsync(
        NuGetTestServerHost server,
        string path) =>
        server.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static VulnerabilitySnapshot CreateVulnerabilitySnapshot()
    {
        var pageUrl = new Uri("https://api.nuget.org/v3-vulnerabilities/base.json");
        return VulnerabilitySnapshot.Parse(
            Encoding.UTF8.GetBytes(
                $$"""
                [
                  {
                    "@name": "base",
                    "@id": "{{pageUrl}}",
                    "@updated": "2026-08-18T12:00:00Z",
                    "comment": "frozen baseline"
                  }
                ]
                """),
            new Dictionary<Uri, ReadOnlyMemory<byte>>
            {
                [pageUrl] = Encoding.UTF8.GetBytes(
                    """
                    {
                      "baseline.package": [
                        {
                          "url": "https://github.com/advisories/GHSA-baseline",
                          "severity": 2,
                          "versions": "[1.0.0, 2.0.0)"
                        }
                      ]
                    }
                    """)
            },
            new Uri("https://api.nuget.org/v3/vulnerabilities/index.json"),
            DateTimeOffset.Parse("2026-08-18T13:00:00Z"));
    }
}
