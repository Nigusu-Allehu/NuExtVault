using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace NuGet.TestServer.Authentication;

public enum AuthenticationProfile
{
    Anonymous,
    NuGetOrg,
    Private,
    PrivateApiKey
}

public sealed class AuthenticationConfiguration
{
    private const int PasswordIterations = 100_000;
    private readonly string? _username;
    private readonly byte[]? _passwordSalt;
    private readonly byte[]? _passwordDigest;
    private readonly byte[]? _apiKeyDigest;

    private AuthenticationConfiguration(
        AuthenticationProfile profile,
        string? username,
        byte[]? passwordSalt,
        byte[]? passwordDigest,
        byte[]? apiKeyDigest)
    {
        Profile = profile;
        _username = username;
        _passwordSalt = passwordSalt;
        _passwordDigest = passwordDigest;
        _apiKeyDigest = apiKeyDigest;
    }

    public static AuthenticationConfiguration Anonymous { get; } =
        new(AuthenticationProfile.Anonymous, null, null, null, null);

    public AuthenticationProfile Profile { get; }
    public bool RequiresBasicAuthentication =>
        Profile is AuthenticationProfile.Private or AuthenticationProfile.PrivateApiKey;
    public bool RequiresApiKeyForWrites =>
        Profile is AuthenticationProfile.NuGetOrg or AuthenticationProfile.PrivateApiKey;

    public static AuthenticationConfiguration Create(
        string? username,
        string? password,
        string? apiKey)
    {
        var hasUsername = !string.IsNullOrEmpty(username);
        var hasPassword = !string.IsNullOrEmpty(password);
        var hasApiKey = !string.IsNullOrEmpty(apiKey);

        if (hasUsername != hasPassword)
        {
            throw new AuthenticationConfigurationException(
                "Username and password must be supplied together.");
        }

        if (username is not null && !hasUsername ||
            password is not null && !hasPassword ||
            apiKey is not null && !hasApiKey)
        {
            throw new AuthenticationConfigurationException(
                "Authentication credentials cannot be empty.");
        }

        if (!hasUsername && !hasApiKey)
        {
            return Anonymous;
        }

        byte[]? passwordSalt = null;
        byte[]? passwordDigest = null;
        if (hasPassword)
        {
            passwordSalt = RandomNumberGenerator.GetBytes(16);
            passwordDigest = DerivePassword(password!, passwordSalt);
        }

        var profile = (hasUsername, hasApiKey) switch
        {
            (false, true) => AuthenticationProfile.NuGetOrg,
            (true, false) => AuthenticationProfile.Private,
            (true, true) => AuthenticationProfile.PrivateApiKey,
            _ => AuthenticationProfile.Anonymous
        };

        return new AuthenticationConfiguration(
            profile,
            username,
            passwordSalt,
            passwordDigest,
            hasApiKey ? HashSecret(apiKey!) : null);
    }

    public bool TryAuthenticateBasic(
        AuthenticationHeaderValue? authorization,
        out string? username)
    {
        username = null;
        if (!RequiresBasicAuthentication ||
            authorization is null ||
            !authorization.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(authorization.Parameter);
        }
        catch (FormatException)
        {
            return false;
        }

        string value;
        try
        {
            value = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(decoded);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var separator = value.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        var candidateUsername = value[..separator];
        var candidatePassword = value[(separator + 1)..];
        if (!string.Equals(candidateUsername, _username, StringComparison.Ordinal))
        {
            return false;
        }

        var candidateDigest = DerivePassword(candidatePassword, _passwordSalt!);
        if (!CryptographicOperations.FixedTimeEquals(candidateDigest, _passwordDigest!))
        {
            return false;
        }

        username = _username;
        return true;
    }

    public bool IsValidApiKey(string? apiKey)
    {
        if (!RequiresApiKeyForWrites || string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            HashSecret(apiKey),
            _apiKeyDigest!);
    }

    private static byte[] DerivePassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);

    private static byte[] HashSecret(string secret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}

public sealed class AuthenticationConfigurationException(string message)
    : Exception(message);
