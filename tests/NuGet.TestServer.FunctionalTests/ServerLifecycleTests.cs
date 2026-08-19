using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.FunctionalTests;

public sealed class ServerLifecycleTests
{
    [Fact]
    public async Task Server_starts_on_loopback_and_advertises_resolvable_resources()
    {
        await using var server = await NuGetTestServerHost.StartAsync();

        Assert.Equal(IPAddress.Loopback, server.BaseUrl.HostNameType is UriHostNameType.IPv4
            ? IPAddress.Parse(server.BaseUrl.Host)
            : null);
        Assert.True(server.Port > 0);

        using var response = await server.HttpClient.GetAsync(server.ServiceIndexUrl);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();

        Assert.Contains(resources, r => r.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0");
        Assert.Contains(resources, r => r.GetProperty("@type").GetString() == "RegistrationsBaseUrl/3.6.0");
        Assert.Contains(resources, r => r.GetProperty("@type").GetString() == "SearchQueryService/3.5.0");
        Assert.Contains(resources, r => r.GetProperty("@type").GetString() == "PackagePublish/2.0.0");
        Assert.Contains(resources, r => r.GetProperty("@type").GetString() == "SymbolPackagePublish/4.9.0");
        Assert.All(resources, r => Assert.True(Uri.TryCreate(r.GetProperty("@id").GetString(), UriKind.Absolute, out _)));

        var health = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/health");
        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.Equal("test", health.GetProperty("mode").GetString());
    }
}
