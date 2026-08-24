using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.RouteFixture;

namespace NuGet.TestServer.FunctionalTests;

/// <summary>
/// Step 11A: every active route is generated from a validated descriptor, so the
/// existing protocol surface, binding failures, access ordering, and streaming
/// behavior must remain identical.
/// </summary>
public sealed class EndpointDescriptorRoutingTests
{
    [Fact]
    public async Task A_separately_compiled_fixture_contributes_a_route_through_descriptors()
    {
        await using var server = await StartWithFlavorsAsync();

        using var index = await server.HttpClient.GetAsync("/flavors/index.json");
        using var filtered = await server.HttpClient.GetAsync("/flavors/index.json?filter=s");
        using var head = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/flavors/index.json"));

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());
        Assert.Equal(
            ["salty", "sweet", "umami"],
            document.RootElement.GetProperty("flavors")
                .EnumerateArray()
                .Select(flavor => flavor.GetString() ?? string.Empty)
                .ToArray());
        using var filteredDocument = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert.Equal(
            ["salty", "sweet"],
            filteredDocument.RootElement.GetProperty("flavors")
                .EnumerateArray()
                .Select(flavor => flavor.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Fixture_routes_do_not_exist_without_the_contribution()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var response = await server.HttpClient.GetAsync("/flavors/index.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Head_mirrors_get_only_where_the_descriptor_declares_it()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var index = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/v3/index.json"));
        using var query = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/query"));
        using var liveness = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/health/live"));

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Empty(await index.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, liveness.StatusCode);
    }

    [Fact]
    public async Task Access_policies_are_enforced_before_request_binding()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "control-key");
        await using var server = await NuGetTestServerHost.StartAsync(authentication);

        using var unauthorized = await server.HttpClient.PostAsync(
            "/__test/packages",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));
        using var authorizedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/__test/packages")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json")
        };
        authorizedRequest.Headers.Add("X-NuGet-ApiKey", "control-key");
        using var authorized = await server.HttpClient.SendAsync(authorizedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, authorized.StatusCode);
    }

    [Fact]
    public async Task Control_upload_binding_preserves_legacy_json_limits_and_errors()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var invalidBase64 = await server.HttpClient.PostAsJsonAsync(
            "/__test/packages",
            new { content = "not-base64!!" });
        using var oversizedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/__test/packages")
        {
            Content = JsonContent.Create(
                new { content = Convert.ToBase64String(new byte[5 * 1024 * 1024]) })
        };
        oversizedRequest.Headers.ExpectContinue = true;
        using var oversized = await server.HttpClient.SendAsync(oversizedRequest);
        using var missingContent = await server.HttpClient.PostAsJsonAsync(
            "/__test/packages",
            new { other = "value" });

        Assert.Equal(HttpStatusCode.BadRequest, invalidBase64.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingContent.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/json")]
    [InlineData("application/jsonx")]
    public async Task Json_bound_control_routes_keep_media_type_and_payload_failures(
        string unsupportedMediaType)
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Bind.Example", "1.0.0").Build());

        using var wrongMediaType = await server.HttpClient.PutAsync(
            "/__test/packages/Bind.Example/1.0.0/metadata",
            new StringContent("owners", Encoding.UTF8, unsupportedMediaType));
        using var malformed = await server.HttpClient.PutAsync(
            "/__test/packages/Bind.Example/1.0.0/metadata",
            new StringContent("{ \"owners\": ", Encoding.UTF8, "application/json"));
        using var valid = await server.HttpClient.PutAsJsonAsync(
            "/__test/packages/Bind.Example/1.0.0/metadata",
            new
            {
                owners = new[] { "alice" },
                downloads = 3,
                verified = true,
                deprecation = (object?)null
            });

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongMediaType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
    }

    [Fact]
    public async Task Binding_failures_are_rendered_without_closing_the_connection_or_corrupting_recording()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var invalid = await server.HttpClient.GetAsync("/query?skip=invalid");
        var requests = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/requests");

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.NotEqual(true, invalid.Headers.ConnectionClose);
        var recorded = Assert.Single(
            requests!,
            request => request.GetProperty("path").GetString() == "/query");
        Assert.Equal(400, recorded.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task Repeated_query_values_preserve_minimal_api_binding_behavior()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var response = await server.HttpClient.GetAsync("/query?skip=1&skip=2");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("take")]
    [InlineData("prerelease")]
    public async Task Empty_typed_query_values_preserve_minimal_api_binding_behavior(string name)
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        using var response = await server.HttpClient.GetAsync($"/query?{name}=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Moderation_binding_failures_stay_client_errors()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Moderated", "1.0.0").Build());

        using var missingReason = await server.HttpClient.PostAsync(
            "/__admin/packages/Moderated/1.0.0/approve",
            content: null);
        using var unknownAction = await server.HttpClient.PostAsync(
            "/__admin/packages/Moderated/1.0.0/frobnicate?reason=because",
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownAction.StatusCode);
    }

    [Fact]
    public async Task Package_content_stays_streamed_and_cancellable()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Streamed.Descriptor", "1.0.0").Build();
        await server.Packages.AddAsync(package);
        const string path =
            "/flatcontainer/streamed.descriptor/1.0.0/streamed.descriptor.1.0.0.nupkg";

        using (var canceled = new CancellationTokenSource())
        {
            using var streaming = await server.HttpClient.GetAsync(
                path,
                HttpCompletionOption.ResponseHeadersRead,
                canceled.Token);
            await using var body = await streaming.Content.ReadAsStreamAsync(canceled.Token);
            var buffer = new byte[16];
            await body.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, canceled.Token);
            await canceled.CancelAsync();
        }

        using var afterCancellation = await server.HttpClient.GetAsync(path);
        using var head = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, path));

        Assert.Equal(HttpStatusCode.OK, afterCancellation.StatusCode);
        Assert.Equal(package.Content.Length, (await afterCancellation.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Multipart_and_raw_uploads_still_reach_the_publication_owner()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Upload.Descriptor", "1.0.0").Build();

        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(package.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(file, "package", "package.nupkg");
        using var uploaded = await server.HttpClient.PutAsync("/package", multipart);
        using var withoutFile = new MultipartFormDataContent
        {
            { new StringContent("no package here"), "note" }
        };
        using var empty = await server.HttpClient.PutAsync("/package", withoutFile);

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, empty.StatusCode);
    }

    private static Task<NuGetTestServerHost> StartWithFlavorsAsync() => FlavorsHost.StartAsync();
}
