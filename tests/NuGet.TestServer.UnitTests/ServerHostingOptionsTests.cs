using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.UnitTests;

public sealed class ServerHostingOptionsTests
{
    [Fact]
    public void Test_mode_preserves_anonymous_loopback_defaults()
    {
        var options = ServerHostingOptions.Create(
            ServerMode.Test,
            "http://127.0.0.1:0",
            AuthenticationConfiguration.Anonymous);

        Assert.Equal(ServerMode.Test, options.Mode);
    }

    [Fact]
    public void Production_mode_rejects_anonymous_writes()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerHostingOptions.Create(
                ServerMode.Production,
                "http://127.0.0.1:0",
                AuthenticationConfiguration.Anonymous));

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_mode_allows_authenticated_writes_on_loopback_http()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");

        var options = ServerHostingOptions.Create(
            ServerMode.Production,
            "http://127.0.0.1:0",
            authentication);

        Assert.Equal(ServerMode.Production, options.Mode);
    }

    [Fact]
    public void Production_mode_rejects_cleartext_non_loopback_listeners()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerHostingOptions.Create(
                ServerMode.Production,
                "http://0.0.0.0:5000",
                authentication));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_mode_allows_https_non_loopback_listeners()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");

        var options = ServerHostingOptions.Create(
            ServerMode.Production,
            "https://packages.example.test:443",
            authentication);

        Assert.Equal(ServerMode.Production, options.Mode);
    }
}
