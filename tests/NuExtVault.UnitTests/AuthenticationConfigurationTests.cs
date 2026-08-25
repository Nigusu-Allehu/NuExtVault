using System.Net.Http.Headers;
using System.Text;
using NuExtVault.Authentication;

namespace NuExtVault.UnitTests;

public sealed class AuthenticationConfigurationTests
{
    [Theory]
    [InlineData(null, null, null, AuthenticationProfile.Anonymous)]
    [InlineData(null, null, "publish-key", AuthenticationProfile.NuGetOrg)]
    [InlineData("user", "password", null, AuthenticationProfile.Private)]
    [InlineData("user", "password", "publish-key", AuthenticationProfile.PrivateApiKey)]
    public void Credentials_derive_the_authentication_profile(
        string? username,
        string? password,
        string? apiKey,
        AuthenticationProfile expected)
    {
        var configuration = AuthenticationConfiguration.Create(username, password, apiKey);

        Assert.Equal(expected, configuration.Profile);
    }

    [Theory]
    [InlineData("user", null, null)]
    [InlineData(null, "password", null)]
    [InlineData("user", null, "publish-key")]
    public void Incomplete_basic_credentials_are_rejected(
        string? username,
        string? password,
        string? apiKey)
    {
        Assert.Throws<AuthenticationConfigurationException>(
            () => AuthenticationConfiguration.Create(username, password, apiKey));
    }

    [Fact]
    public void Basic_verification_handles_colons_and_rejects_bad_headers()
    {
        var configuration = AuthenticationConfiguration.Create(
            "test-user",
            "password:with:colons",
            apiKey: null);
        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("test-user:password:with:colons"));

        Assert.True(configuration.TryAuthenticateBasic(
            new AuthenticationHeaderValue("Basic", encoded),
            out var username));
        Assert.Equal("test-user", username);
        Assert.False(configuration.TryAuthenticateBasic(
            new AuthenticationHeaderValue("Basic", "not-base64"),
            out _));
        Assert.False(configuration.TryAuthenticateBasic(
            new AuthenticationHeaderValue("Bearer", encoded),
            out _));
    }

    [Fact]
    public void Api_key_verification_rejects_missing_and_wrong_values()
    {
        var configuration = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "correct-key");

        Assert.True(configuration.IsValidApiKey("correct-key"));
        Assert.False(configuration.IsValidApiKey("wrong-key"));
        Assert.False(configuration.IsValidApiKey(null));
    }
}
