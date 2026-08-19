using System.Net;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class ProductionSecurityTests
{
    [Fact]
    public async Task Scoped_publish_enforces_namespace_ownership_and_unlist_permission()
    {
        var security = ProductionSecurityConfiguration.Create(
        [
            new(
                "publisher",
                ["publisher-key"],
                [SecurityScope.Read, SecurityScope.Publish],
                ["Contoso."]),
            new(
                "maintainer",
                ["maintainer-key"],
                [SecurityScope.Read, SecurityScope.Publish, SecurityScope.Unlist],
                ["Contoso."]),
            new(
                "administrator",
                ["admin-key"],
                [SecurityScope.Admin],
                ["*"])
        ]);
        await using var server = await NuGetTestServerHost.StartProductionAsync(security);
        var package = TestPackageBuilder.Create("Contoso.Logging", "1.0.0").Build();

        using var missingTransport = CreatePush(package, "publisher-key", forwardedHttps: false);
        using var missingTransportResponse = await server.HttpClient.SendAsync(missingTransport);
        using var wrongNamespace = CreatePush(
            TestPackageBuilder.Create("Fabrikam.Logging", "1.0.0").Build(),
            "publisher-key");
        using var wrongNamespaceResponse = await server.HttpClient.SendAsync(wrongNamespace);
        using var publish = CreatePush(package, "publisher-key");
        using var publishResponse = await server.HttpClient.SendAsync(publish);
        using var foreignVersion = CreatePush(
            TestPackageBuilder.Create("Contoso.Logging", "2.0.0").Build(),
            "maintainer-key");
        using var foreignVersionResponse = await server.HttpClient.SendAsync(foreignVersion);
        using var unlist = CreateRequest(
            HttpMethod.Delete,
            "/package/Contoso.Logging/1.0.0",
            "publisher-key");
        using var unlistResponse = await server.HttpClient.SendAsync(unlist);
        using var adminUnlist = CreateRequest(
            HttpMethod.Delete,
            "/package/Contoso.Logging/1.0.0",
            "admin-key");
        using var adminUnlistResponse = await server.HttpClient.SendAsync(adminUnlist);
        using var publisherDelete = CreateRequest(
            HttpMethod.Delete,
            "/package/Contoso.Logging/1.0.0/hard",
            "publisher-key");
        using var publisherDeleteResponse = await server.HttpClient.SendAsync(publisherDelete);
        using var adminDelete = CreateRequest(
            HttpMethod.Delete,
            "/package/Contoso.Logging/1.0.0/hard",
            "admin-key");
        using var adminDeleteResponse = await server.HttpClient.SendAsync(adminDelete);

        Assert.Equal(HttpStatusCode.UpgradeRequired, missingTransportResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongNamespaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignVersionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlistResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, adminUnlistResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, publisherDeleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, adminDeleteResponse.StatusCode);
        Assert.Contains(
            server.SecurityAudits,
            entry => entry.EventType == SecurityAuditEventType.AuthorizationDenied);
    }

    [Fact]
    public async Task Repeated_bad_credentials_are_throttled_without_blocking_other_clients()
    {
        var security = ProductionSecurityConfiguration.Create(
        [
            new("reader", ["correct-key"], [SecurityScope.Read], ["*"])
        ]);
        await using var server = await NuGetTestServerHost.StartProductionAsync(
            security,
            maximumAuthenticationFailures: 2);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var wrong = CreateRequest(HttpMethod.Get, "/v3/index.json", "wrong-key");
            wrong.Headers.Add("X-Forwarded-For", "192.0.2.10");
            using var response = await server.HttpClient.SendAsync(wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var throttled = CreateRequest(HttpMethod.Get, "/v3/index.json", "correct-key");
        throttled.Headers.Add("X-Forwarded-For", "192.0.2.10");
        using var throttledResponse = await server.HttpClient.SendAsync(throttled);
        using var otherClient = CreateRequest(HttpMethod.Get, "/v3/index.json", "correct-key");
        otherClient.Headers.Add("X-Forwarded-For", "192.0.2.11");
        using var otherClientResponse = await server.HttpClient.SendAsync(otherClient);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttledResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherClientResponse.StatusCode);
    }

    [Fact]
    public async Task Duplicate_publish_cannot_claim_an_existing_unowned_package()
    {
        var security = ProductionSecurityConfiguration.Create(
        [
            new(
                "attacker",
                ["attacker-key"],
                [SecurityScope.Publish, SecurityScope.Unlist],
                ["Contoso."])
        ]);
        await using var server = await NuGetTestServerHost.StartProductionAsync(security);
        var package = TestPackageBuilder.Create("Contoso.Existing", "1.0.0").Build();
        await server.Packages.AddAsync(package);

        using var duplicate = CreatePush(
            TestPackageBuilder.Create("Contoso.Existing", "2.0.0").Build(),
            "attacker-key");
        using var duplicateResponse = await server.HttpClient.SendAsync(duplicate);
        using var unlist = CreateRequest(
            HttpMethod.Delete,
            "/package/Contoso.Existing/1.0.0",
            "attacker-key");
        using var unlistResponse = await server.HttpClient.SendAsync(unlist);

        Assert.Equal(HttpStatusCode.Forbidden, duplicateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlistResponse.StatusCode);
    }

    private static HttpRequestMessage CreatePush(
        TestPackage package,
        string apiKey,
        bool forwardedHttps = true)
    {
        var request = CreateRequest(HttpMethod.Put, "/package", apiKey, forwardedHttps);
        request.Content = new ByteArrayContent(package.Content);
        return request;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string apiKey,
        bool forwardedHttps = true)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-NuGet-ApiKey", apiKey);
        if (forwardedHttps)
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
        }

        return request;
    }
}
