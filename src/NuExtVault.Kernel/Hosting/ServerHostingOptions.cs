using System.Net;
using NuExtVault.Authentication;

namespace NuExtVault.Hosting;

public enum ServerMode
{
    Test,
    Production
}

public sealed record ServerHostingOptions(
    ServerMode Mode,
    string Url,
    AuthenticationConfiguration Authentication,
    TransportSecurityOptions Transport)
{
    public static ServerHostingOptions Create(
        ServerMode mode,
        string url,
        AuthenticationConfiguration authentication,
        TrustedProxyOptions? trustedProxies = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(authentication);
        var transport = new TransportSecurityOptions(trustedProxies);

        if (authentication.Profile == AuthenticationProfile.Production &&
            mode != ServerMode.Production)
        {
            throw new ServerHostingConfigurationException(
                "Production identities require production server mode.");
        }

        if (mode == ServerMode.Production)
        {
            if (authentication.Profile == AuthenticationProfile.Anonymous)
            {
                throw new ServerHostingConfigurationException(
                    "Production mode requires authentication for package writes.");
            }

            foreach (var address in url.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                ValidateProductionAddress(address);
            }

            if (authentication.Profile == AuthenticationProfile.Production &&
                url.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Any(address => new Uri(address).Scheme != Uri.UriSchemeHttps) &&
                transport.TrustedProxies.Count == 0)
            {
                throw new ServerHostingConfigurationException(
                    "Production identity mode requires HTTPS or an explicit trusted reverse proxy.");
            }
        }

        return new ServerHostingOptions(mode, url, authentication, transport);
    }

    private static void ValidateProductionAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ServerHostingConfigurationException(
                $"Production listener '{address}' must be an absolute HTTP or HTTPS URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps || IsLoopback(uri.Host))
        {
            return;
        }

        throw new ServerHostingConfigurationException(
            "Production mode permits cleartext HTTP only on a loopback listener. " +
            "Use HTTPS for a remote listener, or place the loopback server behind a reverse proxy.");
    }

    private static bool IsLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}

public sealed class ServerHostingConfigurationException(string message)
    : Exception(message);
