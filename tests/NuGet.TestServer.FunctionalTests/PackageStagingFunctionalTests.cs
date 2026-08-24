using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.ExternalExtensionTestKit;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

/// <summary>
/// Step 22 ("Package Staging as reference external extension") functional coverage
/// against a real Kestrel host. The staging extension is separately compiled, packed,
/// signed, and loaded through the Step 20 trusted loader; it is absent from every
/// default profile and requires explicit extension roots, trust roots, and grants.
/// </summary>
[Collection(nameof(PackageStagingFunctionalAssetsCollection))]
public sealed class PackageStagingFunctionalTests(PackageStagingFunctionalAssetsFixture fixture)
{
    private const string ApiKey = "staging-admin-key";

    // ---- default absence --------------------------------------------------

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    public async Task Staging_routes_are_absent_from_a_default_host(string profileName)
    {
        await using var server = await NuGetTestServerHost.StartCompositionAsync(
            ServerComposition.Create(
                profileName == "embedded" ? ServerProfiles.Embedded : ServerProfiles.Standard,
                authentication: AuthenticationConfiguration.Anonymous),
            CancellationToken.None);

        using var groups = await server.HttpClient.GetAsync("/staging/groups");
        using var index = await server.HttpClient.GetAsync("/v3/index.json");
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, groups.StatusCode);
        Assert.DoesNotContain(
            document.RootElement.GetProperty("resources").EnumerateArray(),
            entry => (entry.GetProperty("@type").GetString() ?? string.Empty)
                .Contains("Staging", StringComparison.OrdinalIgnoreCase));
    }

    // ---- gateway authorization runs before dispatch ------------------------

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected_before_the_extension_runs()
    {
        await using var server = await StartAsync(requireApiKey: true);

        using var list = await server.HttpClient.GetAsync("/staging/groups");
        using var create = await server.HttpClient.PutAsync("/staging/groups/denied", Json("{}"));
        using var upload = await server.HttpClient.PutAsync(
            "/staging/groups/denied/packages",
            new ByteArrayContent(Nupkg("Contoso.Denied", "1.0.0")));

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);

        // Nothing was dispatched, so no group exists once the caller authenticates.
        using var client = Authorized(server);
        using var group = await client.GetAsync("/staging/groups/denied");
        using var document = await ReadAsync(group);
        Assert.Equal("GroupNotFound", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_authenticated_administrator_is_accepted()
    {
        await using var server = await StartAsync(requireApiKey: true);
        using var client = Authorized(server);

        using var create = await client.PutAsync("/staging/groups/allowed", Json("{}"));
        using var list = await client.GetAsync("/staging/groups");

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = await ReadAsync(create);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("allowed", document.RootElement.GetProperty("groupId").GetString());
    }

    [Fact]
    public async Task An_invalid_api_key_never_authorizes_a_staging_request()
    {
        await using var server = await StartAsync(requireApiKey: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/staging/groups");
        request.Headers.Add("X-NuGet-ApiKey", "not-the-configured-key");
        using var response = await server.HttpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- service resource --------------------------------------------------

    [Fact]
    public async Task The_service_index_advertises_the_staging_resource_with_a_projected_url()
    {
        await using var server = await StartAsync();

        using var index = await server.HttpClient.GetAsync("/v3/index.json");
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());

        var resource = document.RootElement.GetProperty("resources")
            .EnumerateArray()
            .Single(entry => (entry.GetProperty("@type").GetString() ?? string.Empty)
                .StartsWith("NuTest.PackageStaging.ServiceIndex", StringComparison.Ordinal));
        Assert.Equal(
            new Uri(server.BaseUrl, "/staging/groups").AbsoluteUri,
            resource.GetProperty("@id").GetString());
    }

    // ---- upload / inspect / promote ---------------------------------------

    [Fact]
    public async Task Upload_stages_the_real_request_bytes_and_extracts_the_identity()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "flow");
        var content = Nupkg("Contoso.Flow", "1.2.3");

        using var upload = await client.PutAsync(
            "/staging/groups/flow/packages",
            new ByteArrayContent(content));

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var document = await ReadAsync(upload);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Contoso.Flow", document.RootElement.GetProperty("packageId").GetString());
        Assert.Equal("1.2.3", document.RootElement.GetProperty("version").GetString());
        Assert.Equal(content.Length, document.RootElement.GetProperty("contentLength").GetInt64());
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
            document.RootElement.GetProperty("contentSha256").GetString());
    }

    [Fact]
    public async Task An_empty_upload_is_rejected_as_invalid_content()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "empty");

        using var upload = await client.PutAsync(
            "/staging/groups/empty/packages",
            new ByteArrayContent([]));

        using var document = await ReadAsync(upload);
        Assert.Equal("InvalidContent", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_malformed_package_is_rejected_with_redacted_detail()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "malformed");

        using var upload = await client.PutAsync(
            "/staging/groups/malformed/packages",
            new ByteArrayContent("this is not a zip archive"u8.ToArray()));

        using var document = await ReadAsync(upload);
        Assert.Equal("InvalidContent", document.RootElement.GetProperty("outcome").GetString());
        var detail = document.RootElement.GetProperty("detail").GetString() ?? string.Empty;
        Assert.DoesNotContain(Path.DirectorySeparatorChar, detail);
        Assert.DoesNotContain("NuGet.TestServer.", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_reports_the_staged_record()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "inspect");
        await UploadAsync(client, "inspect", "Contoso.Inspect", "1.0.0");

        using var inspect = await client.GetAsync(
            "/staging/groups/inspect/packages/Contoso.Inspect/1.0.0");

        using var document = await ReadAsync(inspect);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "Staged",
            document.RootElement.GetProperty("package").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Staged_packages_are_invisible_until_promotion_and_visible_immediately_after()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "visibility");
        await UploadAsync(client, "visibility", "Contoso.Visible", "2.0.0");

        await AssertAbsentAsync(client, "Contoso.Visible", "2.0.0");

        using var promote = await PromoteAsync(
            client, "visibility", "Contoso.Visible", "2.0.0", "v-1");

        using var document = await ReadAsync(promote);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        await AssertPresentAsync(client, "Contoso.Visible", "2.0.0");
    }

    [Fact]
    public async Task A_promoted_package_is_downloadable_and_searchable()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "download");
        var content = await UploadAsync(client, "download", "Contoso.Download", "3.1.0");
        using var promote = await PromoteAsync(
            client, "download", "Contoso.Download", "3.1.0", "d-1");
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        using var download = await client.GetAsync(
            "/flatcontainer/contoso.download/3.1.0/contoso.download.3.1.0.nupkg");
        using var search = await client.GetAsync("/query?q=Contoso.Download");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        using var searchDocument = await ReadAsync(search);
        Assert.True(searchDocument.RootElement.GetProperty("totalHits").GetInt32() >= 1);
    }

    [Fact]
    public async Task Promoting_a_package_publishes_its_staged_symbols()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "symbols");
        await UploadAsync(client, "symbols", "Contoso.Symbols", "1.0.0");
        using var symbols = TestPackageBuilder.Create("Contoso.Symbols", "1.0.0")
            .WithFile("lib/net10.0/Contoso.Symbols.pdb", [1, 2, 3, 4])
            .Build();

        using var upload = await client.PutAsync(
            "/staging/groups/symbols/packages/Contoso.Symbols/1.0.0/symbols",
            new ByteArrayContent(symbols.Content));
        using var promote = await PromoteAsync(
            client,
            "symbols",
            "Contoso.Symbols",
            "1.0.0",
            "symbols-1");

        using var uploadDocument = await ReadAsync(upload);
        using var promoteDocument = await ReadAsync(promote);
        Assert.Equal("Succeeded", uploadDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Succeeded", promoteDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            symbols.Content,
            await server.Packages.FindSymbolAsync("Contoso.Symbols", "1.0.0"));
    }

    [Fact]
    public async Task Promotion_is_idempotent_for_the_same_key()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "idem");
        await UploadAsync(client, "idem", "Contoso.Idem", "1.0.0");

        using var first = await PromoteAsync(client, "idem", "Contoso.Idem", "1.0.0", "key-1");
        using var second = await PromoteAsync(client, "idem", "Contoso.Idem", "1.0.0", "key-1");

        using var firstDocument = await ReadAsync(first);
        using var secondDocument = await ReadAsync(second);
        Assert.Equal("Succeeded", firstDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "AlreadyResolved",
            secondDocument.RootElement.GetProperty("outcome").GetString());
        Assert.True(secondDocument.RootElement.GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Upload_is_idempotent_for_the_same_key()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "upload-idem");
        var content = Nupkg("Contoso.UploadIdem", "1.0.0");

        using var first = await UploadWithKeyAsync(client, "upload-idem", content, "u-1");
        using var second = await UploadWithKeyAsync(client, "upload-idem", content, "u-1");
        using var group = await client.GetAsync("/staging/groups/upload-idem");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var document = await ReadAsync(group);
        Assert.Single(
            document.RootElement.GetProperty("group").GetProperty("packages").EnumerateArray());
    }

    [Fact]
    public async Task A_duplicate_package_version_in_one_group_is_reported()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "dupe");
        await UploadAsync(client, "dupe", "Contoso.Dupe", "1.0.0");

        using var duplicate = await client.PutAsync(
            "/staging/groups/dupe/packages",
            new ByteArrayContent(Nupkg("Contoso.Dupe", "1.0.0")));

        using var document = await ReadAsync(duplicate);
        Assert.Equal("DuplicatePackage", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Promoting_content_that_is_already_published_reports_a_duplicate()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "first");
        await CreateGroupAsync(client, "second");
        await UploadAsync(client, "first", "Contoso.Twice", "1.0.0");
        await UploadAsync(client, "second", "Contoso.Twice", "1.0.0");

        using var promoted = await PromoteAsync(client, "first", "Contoso.Twice", "1.0.0", "a");
        using var again = await PromoteAsync(client, "second", "Contoso.Twice", "1.0.0", "b");

        using var promotedDocument = await ReadAsync(promoted);
        using var againDocument = await ReadAsync(again);
        Assert.Equal("Succeeded", promotedDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "DuplicatePackage",
            againDocument.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_concurrent_promotion_never_publishes_a_second_time()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "race");
        await UploadAsync(client, "race", "Contoso.Race", "1.0.0");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            PromoteAsync(client, "race", "Contoso.Race", "1.0.0", $"race-{index}")));

        var outcomes = new List<string>();
        foreach (var response in responses)
        {
            using var document = await ReadAsync(response);
            outcomes.Add(document.RootElement.GetProperty("outcome").GetString() ?? string.Empty);
            response.Dispose();
        }

        Assert.Single(outcomes, outcome => outcome == "Succeeded");
        Assert.All(
            outcomes.Where(outcome => outcome != "Succeeded"),
            outcome => Assert.Contains(
                outcome,
                new[] { "AlreadyResolved", "Conflict", "PackageNotFound", "DuplicatePackage" }));
        await AssertPresentAsync(client, "Contoso.Race", "1.0.0");
        using var flat = await client.GetAsync("/flatcontainer/contoso.race/index.json");
        using var flatDocument = await ReadAsync(flat);
        Assert.Single(flatDocument.RootElement.GetProperty("versions").EnumerateArray());
    }

    // ---- reject / expire ---------------------------------------------------

    [Fact]
    public async Task Reject_resolves_the_record_and_blocks_later_promotion()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "reject");
        await UploadAsync(client, "reject", "Contoso.Reject", "1.0.0");

        using var rejected = await client.PostAsync(
            "/staging/groups/reject/packages/Contoso.Reject/1.0.0/reject",
            Json("{\"reason\":\"policy\"}"));
        using var promote = await PromoteAsync(client, "reject", "Contoso.Reject", "1.0.0", "r");

        using var rejectedDocument = await ReadAsync(rejected);
        using var promoteDocument = await ReadAsync(promote);
        Assert.Equal("Succeeded", rejectedDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "AlreadyResolved",
            promoteDocument.RootElement.GetProperty("outcome").GetString());
        await AssertAbsentAsync(client, "Contoso.Reject", "1.0.0");
    }

    [Fact]
    public async Task Reject_reports_typed_outcomes_for_unknown_groups_and_packages()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "typed");

        using var missingGroup = await client.PostAsync(
            "/staging/groups/nonexistent/packages/p/1.0.0/reject",
            Json("{}"));
        using var missingPackage = await client.PostAsync(
            "/staging/groups/typed/packages/p/1.0.0/reject",
            Json("{}"));

        using var missingGroupDocument = await ReadAsync(missingGroup);
        using var missingPackageDocument = await ReadAsync(missingPackage);
        Assert.Equal(
            "GroupNotFound",
            missingGroupDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "PackageNotFound",
            missingPackageDocument.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Expire_resolves_every_staged_record_and_blocks_uploads()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "expire");
        await UploadAsync(client, "expire", "Contoso.Expire", "1.0.0");

        using var expired = await client.PostAsync("/staging/groups/expire/expire", null);
        using var upload = await client.PutAsync(
            "/staging/groups/expire/packages",
            new ByteArrayContent(Nupkg("Contoso.Later", "1.0.0")));

        using var expiredDocument = await ReadAsync(expired);
        using var uploadDocument = await ReadAsync(upload);
        Assert.Equal("Succeeded", expiredDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, expiredDocument.RootElement.GetProperty("expiredPackages").GetInt32());
        Assert.Equal(
            "GroupInactive",
            uploadDocument.RootElement.GetProperty("outcome").GetString());
        await AssertAbsentAsync(client, "Contoso.Expire", "1.0.0");
    }

    [Fact]
    public async Task Expire_reports_a_typed_outcome_for_an_unknown_group()
    {
        await using var server = await StartAsync();

        using var response = await server.HttpClient.PostAsync(
            "/staging/groups/nonexistent/expire",
            null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadAsync(response);
        Assert.Equal("GroupNotFound", document.RootElement.GetProperty("outcome").GetString());
    }

    // ---- quota / limits ----------------------------------------------------

    [Fact]
    public async Task A_group_package_quota_is_enforced()
    {
        await using var server = await StartAsync();
        var client = server.HttpClient;
        await CreateGroupAsync(client, "quota", "{\"maximumPackages\":1}");
        await UploadAsync(client, "quota", "Contoso.One", "1.0.0");

        using var second = await client.PutAsync(
            "/staging/groups/quota/packages",
            new ByteArrayContent(Nupkg("Contoso.Two", "1.0.0")));

        using var document = await ReadAsync(second);
        Assert.Equal("QuotaExceeded", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_body_above_the_declared_route_limit_is_rejected_by_the_gateway()
    {
        await using var server = await StartAsync();
        await CreateGroupAsync(server.HttpClient, "toolarge");

        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var response = await SendOversizedHeadersAsync(server);

            Assert.Contains(" 413 ", response, StringComparison.Ordinal);
            Assert.True(
                response.Contains(
                    "The request body exceeds the declared route limit.",
                    StringComparison.Ordinal),
                $"Attempt {attempt} did not return the gateway rejection detail.");
        }
    }

    // ---- durability --------------------------------------------------------

    [Fact]
    public async Task Staged_groups_and_content_survive_a_restart_with_no_orphans()
    {
        var storage = CreateStorage();
        var assets = fixture.StagingAssets;
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(publisher: "NuTest");
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "staging.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(assets, key));

        await using (var first = await NuGetTestServerHost.StartCompositionAsync(
            ServerComposition.Create(
                StagingProfile(ServerProfiles.Standard),
                storageDirectory: storage,
                authentication: AuthenticationConfiguration.Anonymous,
                externalExtensions: new ExternalExtensionConfiguration(
                    [.. roots.Roots],
                    [trustRoot],
                    TimeProvider.System)),
            CancellationToken.None))
        {
            await CreateGroupAsync(first.HttpClient, "durable");
            await UploadAsync(first.HttpClient, "durable", "Contoso.Durable", "1.0.0");
        }

        await using var restarted = await NuGetTestServerHost.StartCompositionAsync(
            ServerComposition.Create(
                StagingProfile(ServerProfiles.Standard),
                storageDirectory: storage,
                authentication: AuthenticationConfiguration.Anonymous,
                externalExtensions: new ExternalExtensionConfiguration(
                    [.. roots.Roots],
                    [trustRoot],
                    TimeProvider.System)),
            CancellationToken.None);

        using var group = await restarted.HttpClient.GetAsync("/staging/groups/durable");
        using var document = await ReadAsync(group);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        var staged = Assert.Single(
            document.RootElement.GetProperty("group").GetProperty("packages").EnumerateArray());
        Assert.Equal("Staged", staged.GetProperty("status").GetString());
        await AssertAbsentAsync(restarted.HttpClient, "Contoso.Durable", "1.0.0");

        using var promote = await PromoteAsync(
            restarted.HttpClient, "durable", "Contoso.Durable", "1.0.0", "after-restart");
        using var promoteDocument = await ReadAsync(promote);
        Assert.Equal("Succeeded", promoteDocument.RootElement.GetProperty("outcome").GetString());
        await AssertPresentAsync(restarted.HttpClient, "Contoso.Durable", "1.0.0");
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            !string.IsNullOrWhiteSpace(body),
            $"Expected a JSON body from {response.RequestMessage?.Method} " +
            $"{response.RequestMessage?.RequestUri} but got {(int)response.StatusCode} with an " +
            "empty payload.");
        return JsonDocument.Parse(body);
    }

    private static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");

    internal static byte[] Nupkg(string id, string version)
    {
        using var package = TestPackageBuilder.Create(id, version).Build();
        return package.Content;
    }

    private static async Task CreateGroupAsync(
        HttpClient client,
        string groupId,
        string body = "{}")
    {
        using var response = await client.PutAsync($"/staging/groups/{groupId}", Json(body));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<byte[]> UploadAsync(
        HttpClient client,
        string groupId,
        string packageId,
        string version)
    {
        var content = Nupkg(packageId, version);
        using var response = await client.PutAsync(
            $"/staging/groups/{groupId}/packages",
            new ByteArrayContent(content));
        response.EnsureSuccessStatusCode();
        using var document = await ReadAsync(response);
        Assert.Equal("Succeeded", document.RootElement.GetProperty("outcome").GetString());
        return content;
    }

    private static Task<HttpResponseMessage> UploadWithKeyAsync(
        HttpClient client,
        string groupId,
        byte[] content,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/staging/groups/{groupId}/packages")
        {
            Content = new ByteArrayContent(content)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PromoteAsync(
        HttpClient client,
        string groupId,
        string packageId,
        string version,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/staging/groups/{groupId}/packages/{packageId}/{version}/promote");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static async Task AssertAbsentAsync(HttpClient client, string id, string version)
    {
        var lowered = id.ToLowerInvariant();
        using var flat = await client.GetAsync($"/flatcontainer/{lowered}/index.json");
        using var registration = await client.GetAsync($"/registration/{lowered}/index.json");
        using var search = await client.GetAsync($"/query?q={id}");
        using var download = await client.GetAsync(
            $"/flatcontainer/{lowered}/{version}/{lowered}.{version}.nupkg");
        using var searchDocument = await ReadAsync(search);

        Assert.Equal(HttpStatusCode.NotFound, flat.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, registration.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(0, searchDocument.RootElement.GetProperty("totalHits").GetInt32());
    }

    private static async Task AssertPresentAsync(HttpClient client, string id, string version)
    {
        var lowered = id.ToLowerInvariant();
        using var flat = await client.GetAsync($"/flatcontainer/{lowered}/index.json");
        using var registration = await client.GetAsync($"/registration/{lowered}/index.json");
        using var flatDocument = await ReadAsync(flat);

        Assert.Equal(HttpStatusCode.OK, flat.StatusCode);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        Assert.Contains(
            flatDocument.RootElement.GetProperty("versions").EnumerateArray()
                .Select(entry => entry.GetString()),
            candidate => candidate == version);
    }

    private static HttpClient Authorized(NuGetTestServerHost server)
    {
        var client = new HttpClient { BaseAddress = server.BaseUrl };
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", ApiKey);
        return client;
    }

    private static async Task<string> SendOversizedHeadersAsync(NuGetTestServerHost server)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await using var stream = client.GetStream();
        var headers = Encoding.ASCII.GetBytes(
            $"PUT /staging/groups/toolarge/packages HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            $"Content-Length: {17 * 1024 * 1024}\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        var response = new StringBuilder();
        var buffer = new byte[4096];
        while (!response.ToString().Contains(
                   "The request body exceeds the declared route limit.",
                   StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            response.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return response.ToString();
    }

    private static string CreateStorage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "nutestserver-staging-functional",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private Task<NuGetTestServerHost> StartAsync(bool requireApiKey = false)
    {
        var assets = fixture.StagingAssets;
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(publisher: "NuTest");
        var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "staging.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(assets, key));
        return NuGetTestServerHost.StartCompositionAsync(
            ServerComposition.Create(
                StagingProfile(ServerProfiles.Embedded),
                authentication: requireApiKey
                    ? AuthenticationConfiguration.Create(null, null, ApiKey)
                    : AuthenticationConfiguration.Anonymous,
                externalExtensions: new ExternalExtensionConfiguration(
                    [.. roots.Roots],
                    [trustRoot],
                    TimeProvider.System)),
            CancellationToken.None);
    }

    private static ServerProfile StagingProfile(ServerProfile profile) =>
        profile with
        {
            Grants =
            [
                .. profile.Grants,
                new CapabilityGrant(BuiltInCapabilityNames.HostClockRead),
                new CapabilityGrant(BuiltInCapabilityNames.ExtensionStateRead),
                new CapabilityGrant(BuiltInCapabilityNames.ExtensionStateWrite),
                new CapabilityGrant(BuiltInCapabilityNames.PackageContentWriteStaged),
                new CapabilityGrant(BuiltInCapabilityNames.PublicationRequest)
            ]
        };
}

/// <summary>Packs the optional staging extension once for the whole collection.</summary>
public sealed class PackageStagingFunctionalAssetsFixture : IAsyncLifetime
{
    public ContosoFlavorsAssets StagingAssets { get; private set; } = null!;

    public async Task InitializeAsync() =>
        StagingAssets = await PackageStagingAssets.BuildAsync("staging-functional");

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(nameof(PackageStagingFunctionalAssetsCollection))]
public sealed class PackageStagingFunctionalAssetsCollection :
    ICollectionFixture<PackageStagingFunctionalAssetsFixture>;
