using System.Net;

namespace NuExtVault.Hosting;

public sealed record TrustedProxyOptions(IReadOnlyList<string> Addresses);

public sealed class TransportSecurityOptions
{
    private readonly HashSet<IPAddress> _trustedProxies;

    public TransportSecurityOptions(TrustedProxyOptions? proxies)
    {
        _trustedProxies = (proxies?.Addresses ?? [])
            .Select(address => IPAddress.TryParse(address, out var parsed)
                ? parsed
                : throw new ServerHostingConfigurationException(
                    $"Trusted proxy '{address}' must be an IP address."))
            .ToHashSet();
    }

    public IReadOnlySet<IPAddress> TrustedProxies => _trustedProxies;

    public bool IsTrustedProxy(IPAddress? address) =>
        address is not null && _trustedProxies.Contains(address);
}
