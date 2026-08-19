using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class ProductionModeTests
{
    [Fact]
    public async Task Production_mode_exposes_health_and_omits_test_control_routes()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        await using var server = await NuGetTestServerHost.StartAsync(
            ServerMode.Production,
            authentication);
        await server.Faults.AddAsync(new FaultRule(
            Id: "disabled",
            Method: "GET",
            PathContains: "/v3/index.json",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 1,
            Delay: TimeSpan.Zero));

        var health = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/health");
        using var index = await server.HttpClient.GetAsync("/v3/index.json");
        using var state = await server.HttpClient.GetAsync("/__test/state");
        using var reset = await server.HttpClient.PostAsync("/__test/reset", null);
        using var packages = await server.HttpClient.GetAsync("/__test/packages");
        using var requests = await server.HttpClient.GetAsync("/__test/requests");
        using var faults = await server.HttpClient.GetAsync("/__test/faults");

        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.Equal("production", health.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, packages.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, requests.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, faults.StatusCode);
        Assert.Empty(await server.Requests.GetAsync());
    }

    [Fact]
    public async Task Default_test_mode_retains_controls_and_identifies_itself()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        var health = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/health");
        using var state = await server.HttpClient.GetAsync("/__test/state");

        Assert.Equal("test", health.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    [Fact]
    public async Task Legacy_production_api_key_protects_all_mutations()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        await using var server = await NuGetTestServerHost.StartAsync(
            ServerMode.Production,
            authentication);
        var package = TestPackageBuilder.Create("Legacy.Production", "1.0.0").Build();

        using var anonymous = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));
        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Put, "/package")
        {
            Content = new ByteArrayContent(package.Content)
        };
        authenticatedRequest.Headers.Add("X-NuGet-ApiKey", "publish-key");
        using var authenticated = await server.HttpClient.SendAsync(authenticatedRequest);
        using var anonymousModeration = await server.HttpClient.PostAsync(
            "/__admin/packages/Legacy.Production/1.0.0/quarantine?reason=test",
            null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Created, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousModeration.StatusCode);
    }
}
