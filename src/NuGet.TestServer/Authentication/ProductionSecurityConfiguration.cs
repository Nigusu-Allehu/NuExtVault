using System.Security.Cryptography;
using System.Text;

namespace NuGet.TestServer.Authentication;

public enum SecurityScope
{
    Read,
    Publish,
    Unlist,
    Delete,
    Admin
}

public sealed record ProductionIdentityOptions(
    string Name,
    IReadOnlyList<string> ApiKeys,
    IReadOnlyList<SecurityScope> Scopes,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string>? Passwords = null);

public sealed class ProductionIdentity
{
    private readonly HashSet<SecurityScope> _scopes;
    private readonly string[] _namespaces;

    internal ProductionIdentity(ProductionIdentityOptions options)
    {
        Name = options.Name;
        _scopes = options.Scopes.ToHashSet();
        _namespaces = options.Namespaces.ToArray();
    }

    public string Name { get; }

    public bool HasScope(SecurityScope scope) =>
        _scopes.Contains(SecurityScope.Admin) || _scopes.Contains(scope);

    public bool AllowsPackage(string packageId) =>
        _scopes.Contains(SecurityScope.Admin) ||
        _namespaces.Any(prefix =>
            prefix == "*" || packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

public sealed class ProductionSecurityConfiguration
{
    private const int SecretIterations = 100_000;
    private readonly Credential[] _apiKeys;
    private readonly IReadOnlyDictionary<string, BasicIdentity> _basicIdentities;

    private ProductionSecurityConfiguration(
        Credential[] apiKeys,
        IReadOnlyDictionary<string, BasicIdentity> basicIdentities)
    {
        _apiKeys = apiKeys;
        _basicIdentities = basicIdentities;
    }

    public static ProductionSecurityConfiguration Create(
        IReadOnlyList<ProductionIdentityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new AuthenticationConfigurationException(
                "At least one production identity must be configured.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var clearCredentials = new HashSet<string>(StringComparer.Ordinal);
        var apiKeys = new List<Credential>();
        var basic = new Dictionary<string, BasicIdentity>(StringComparer.Ordinal);
        foreach (var item in options)
        {
            if (item is null ||
                item.ApiKeys is null ||
                item.Scopes is null ||
                item.Namespaces is null)
            {
                throw new AuthenticationConfigurationException(
                    "Production identities require name, credentials, scopes, and namespaces.");
            }

            if (string.IsNullOrWhiteSpace(item.Name) || !names.Add(item.Name))
            {
                throw new AuthenticationConfigurationException(
                    "Production identity names must be non-empty and unique.");
            }

            if (item.Scopes.Count == 0)
            {
                throw new AuthenticationConfigurationException(
                    $"Identity '{item.Name}' must have at least one scope.");
            }

            if (item.Namespaces.Count == 0 ||
                item.Namespaces.Any(string.IsNullOrWhiteSpace))
            {
                throw new AuthenticationConfigurationException(
                    $"Identity '{item.Name}' must have at least one package namespace.");
            }

            var identity = new ProductionIdentity(item);
            foreach (var apiKey in item.ApiKeys)
            {
                ValidateCredential(apiKey, clearCredentials);
                apiKeys.Add(Credential.Create(apiKey, identity));
            }

            var passwords = item.Passwords ?? [];
            foreach (var password in passwords)
            {
                ValidateCredential(password, clearCredentials);
            }

            if (passwords.Count > 0)
            {
                basic.Add(
                    item.Name,
                    new BasicIdentity(
                        identity,
                        passwords.Select(password => Credential.Create(password, identity)).ToArray()));
            }

            if (item.ApiKeys.Count == 0 && passwords.Count == 0)
            {
                throw new AuthenticationConfigurationException(
                    $"Identity '{item.Name}' must have at least one credential.");
            }
        }

        return new ProductionSecurityConfiguration(apiKeys.ToArray(), basic);
    }

    public bool TryAuthenticateApiKey(string? apiKey, out ProductionIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        foreach (var credential in _apiKeys)
        {
            if (credential.Verify(apiKey))
            {
                identity = credential.Identity;
                return true;
            }
        }

        return false;
    }

    public bool TryAuthenticateBasic(
        string username,
        string password,
        out ProductionIdentity? identity)
    {
        identity = null;
        if (!_basicIdentities.TryGetValue(username, out var basic))
        {
            return false;
        }

        foreach (var credential in basic.Passwords)
        {
            if (credential.Verify(password))
            {
                identity = basic.Identity;
                return true;
            }
        }

        return false;
    }

    private static void ValidateCredential(string value, ISet<string> credentials)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new AuthenticationConfigurationException(
                "Production credentials cannot be empty.");
        }

        if (!credentials.Add(value))
        {
            throw new AuthenticationConfigurationException(
                "Production credentials must be unique across identities.");
        }
    }

    private sealed record BasicIdentity(
        ProductionIdentity Identity,
        Credential[] Passwords);

    private sealed class Credential
    {
        private readonly byte[] _salt;
        private readonly byte[] _digest;

        private Credential(
            byte[] salt,
            byte[] digest,
            ProductionIdentity identity)
        {
            _salt = salt;
            _digest = digest;
            Identity = identity;
        }

        public ProductionIdentity Identity { get; }

        public static Credential Create(string secret, ProductionIdentity identity)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            return new Credential(salt, Derive(secret, salt), identity);
        }

        public bool Verify(string secret) =>
            CryptographicOperations.FixedTimeEquals(Derive(secret, _salt), _digest);

        private static byte[] Derive(string secret, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(secret),
                salt,
                SecretIterations,
                HashAlgorithmName.SHA256,
                32);
    }
}
