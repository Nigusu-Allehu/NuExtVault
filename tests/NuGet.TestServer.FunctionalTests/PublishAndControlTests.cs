using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class PublishAndControlTests
{
    [Fact]
    public async Task Raw_nupkg_can_be_pushed_and_duplicate_push_conflicts()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Pushed.Package", "1.0.0").Build();

        using var first = await server.HttpClient.PutAsync("/package", new ByteArrayContent(package.Content));
        using var duplicate = await server.HttpClient.PutAsync("/package", new ByteArrayContent(package.Content));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.NotNull(await server.Packages.FindAsync("pushed.package", "1.0.0"));
    }

    [Fact]
    public async Task Control_api_can_add_list_and_reset_packages()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Controlled.Package", "1.0.0").Build();

        using var add = await server.HttpClient.PostAsJsonAsync("/__test/packages", new
        {
            content = Convert.ToBase64String(package.Content)
        });
        add.EnsureSuccessStatusCode();

        var packages = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/packages");
        Assert.Single(packages!);

        using var reset = await server.HttpClient.PostAsync("/__test/reset", null);
        reset.EnsureSuccessStatusCode();
        packages = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/packages");
        Assert.Empty(packages!);
    }

    [Fact]
    public async Task Faults_are_deterministic_and_requests_are_recorded()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example", "1.0.0").Build());
        await server.Faults.AddAsync(new FaultRule(
            Id: "fail-twice",
            Method: "GET",
            PathContains: "/flatcontainer/example/1.0.0/",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 2,
            Delay: TimeSpan.Zero));

        const string path = "/flatcontainer/example/1.0.0/example.1.0.0.nupkg";
        using var first = await server.HttpClient.GetAsync(path);
        using var second = await server.HttpClient.GetAsync(path);
        using var third = await server.HttpClient.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var requests = await server.Requests.GetAsync();
        var attempts = requests.Where(r => r.Path == path).ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.Equal(["fail-twice", "fail-twice", null], attempts.Select(r => r.FaultRuleId));
        Assert.Equal([503, 503, 200], attempts.Select(r => r.StatusCode));
    }

    [Fact]
    public async Task Control_api_reports_bounded_runtime_state_and_reset_clears_it()
    {
        await using var server = await NuGetTestServerHost.StartAsync(
            new RuntimeStateConfiguration(requestHistoryCapacity: 3, faultRuleCapacity: 2));

        for (var index = 0; index < 5; index++)
        {
            using var health = await server.HttpClient.GetAsync("/__test/health");
            health.EnsureSuccessStatusCode();
        }

        var requests = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/requests");
        Assert.Equal(3, requests!.Length);

        using var firstFault = await AddFaultAsync(server, "first");
        using var secondFault = await AddFaultAsync(server, "second");
        using var rejectedFault = await AddFaultAsync(server, "third");
        Assert.Equal(HttpStatusCode.Created, firstFault.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondFault.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejectedFault.StatusCode);

        var state = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/state");
        Assert.Equal(3, state.GetProperty("requestCount").GetInt32());
        Assert.True(state.GetProperty("evictedRequestCount").GetInt64() >= 3);
        Assert.Equal(3, state.GetProperty("requestCapacity").GetInt32());
        Assert.Equal(2, state.GetProperty("faultCount").GetInt32());
        Assert.Equal(2, state.GetProperty("faultCapacity").GetInt32());

        using var reset = await server.HttpClient.PostAsync("/__test/reset", null);
        reset.EnsureSuccessStatusCode();

        state = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/state");
        Assert.Equal(0, state.GetProperty("requestCount").GetInt32());
        Assert.Equal(0, state.GetProperty("evictedRequestCount").GetInt64());
        Assert.Equal(0, state.GetProperty("faultCount").GetInt32());
    }

    private static Task<HttpResponseMessage> AddFaultAsync(
        NuGetTestServerHost server,
        string id) =>
        server.HttpClient.PostAsJsonAsync("/__test/faults", new FaultRule(
            Id: id,
            Method: "GET",
            PathContains: "/flatcontainer/",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 1,
            Delay: TimeSpan.Zero));
}
