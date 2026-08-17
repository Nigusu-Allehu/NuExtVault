using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task NuGetOrg_profile_allows_reads_but_requires_api_key_for_writes_and_control()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        await using var server = await NuGetTestServerHost.StartAsync(authentication);
        var package = TestPackageBuilder.Create("Secured.Package", "1.0.0").Build();

        using var index = await server.HttpClient.GetAsync("/v3/index.json");
        using var health = await server.HttpClient.GetAsync("/__test/health");
        using var control = await server.HttpClient.GetAsync("/__test/packages");
        using var missing = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));
        using var wrongRequest = new HttpRequestMessage(HttpMethod.Put, "/package")
        {
            Content = new ByteArrayContent(package.Content)
        };
        wrongRequest.Headers.Add("X-NuGet-ApiKey", "wrong-key");
        using var wrong = await server.HttpClient.SendAsync(wrongRequest);
        using var correctRequest = new HttpRequestMessage(HttpMethod.Put, "/package")
        {
            Content = new ByteArrayContent(package.Content)
        };
        correctRequest.Headers.Add("X-NuGet-ApiKey", "publish-key");
        using var correct = await server.HttpClient.SendAsync(correctRequest);

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, control.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Null(wrong.Headers.WwwAuthenticate.FirstOrDefault());
        Assert.Equal(HttpStatusCode.Created, correct.StatusCode);
    }

    [Fact]
    public async Task Private_profile_challenges_anonymous_requests_and_accepts_basic_credentials()
    {
        var authentication = AuthenticationConfiguration.Create(
            "test-user",
            "test-password",
            apiKey: null);
        await using var server = await NuGetTestServerHost.StartAsync(authentication);

        using var anonymous = await server.HttpClient.GetAsync("/v3/index.json");
        using var wrongRequest = CreateBasicRequest(
            HttpMethod.Get,
            "/v3/index.json",
            "test-user",
            "wrong-password");
        using var wrong = await server.HttpClient.SendAsync(wrongRequest);
        using var correctRequest = CreateBasicRequest(
            HttpMethod.Get,
            "/v3/index.json",
            "test-user",
            "test-password");
        using var correct = await server.HttpClient.SendAsync(correctRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains(
            anonymous.Headers.WwwAuthenticate,
            value => value.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);

        var requests = await server.Requests.GetAsync();
        Assert.Equal("test-user", requests.Last().AuthenticatedUser);
    }

    [Fact]
    public async Task Private_api_key_profile_requires_basic_and_key_for_push()
    {
        var authentication = AuthenticationConfiguration.Create(
            "test-user",
            "test-password",
            "publish-key");
        await using var server = await NuGetTestServerHost.StartAsync(authentication);
        var package = TestPackageBuilder.Create("Strict.Package", "1.0.0").Build();

        using var basicOnly = CreateBasicRequest(
            HttpMethod.Put,
            "/package",
            "test-user",
            "test-password",
            package.Content);
        using var rejected = await server.HttpClient.SendAsync(basicOnly);

        using var complete = CreateBasicRequest(
            HttpMethod.Put,
            "/package",
            "test-user",
            "test-password",
            package.Content);
        complete.Headers.Add("X-NuGet-ApiKey", "publish-key");
        using var accepted = await server.HttpClient.SendAsync(complete);

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static HttpRequestMessage CreateBasicRequest(
        HttpMethod method,
        string path,
        string username,
        string password,
        byte[]? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        if (content is not null)
        {
            request.Content = new ByteArrayContent(content);
        }

        return request;
    }
}
