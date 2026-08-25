using NuExtVault.Authentication;
using NuExtVault.Cli;

namespace NuExtVault.FunctionalTests;

public sealed class AuthenticationCliOptionsTests
{
    [Fact]
    public void Api_key_environment_option_derives_nuget_org_profile()
    {
        var options = AuthenticationCliOptions.Parse(
            ["start", "--api-key-env", "TEST_API_KEY"],
            name => name == "TEST_API_KEY" ? "publish-key" : null);

        Assert.Equal(AuthenticationProfile.NuGetOrg, options.Configuration.Profile);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Username_and_password_environment_option_derive_private_profile()
    {
        var options = AuthenticationCliOptions.Parse(
            ["start", "--username", "user", "--password-env", "TEST_PASSWORD"],
            name => name == "TEST_PASSWORD" ? "password" : null);

        Assert.Equal(AuthenticationProfile.Private, options.Configuration.Profile);
    }

    [Fact]
    public void Literal_secrets_generate_warnings()
    {
        var options = AuthenticationCliOptions.Parse(
            ["start", "--api-key", "publish-key"],
            _ => null);

        Assert.Single(options.Warnings);
    }

    [Fact]
    public void Missing_environment_variable_is_a_configuration_error()
    {
        Assert.Throws<CliConfigurationException>(() =>
            AuthenticationCliOptions.Parse(
                ["start", "--api-key-env", "MISSING_KEY"],
                _ => null));
    }

    [Fact]
    public void Generated_key_derives_nuget_org_profile()
    {
        var options = AuthenticationCliOptions.Parse(
            ["start", "--generate-api-key"],
            _ => null,
            () => "generated-key");

        Assert.Equal(AuthenticationProfile.NuGetOrg, options.Configuration.Profile);
        Assert.Equal("generated-key", options.GeneratedApiKey);
    }

    [Fact]
    public void Production_identity_json_is_loaded_from_an_environment_provider()
    {
        const string json =
            """
            {
              "identities": [
                {
                  "name": "publisher",
                  "apiKeys": ["rotated-key"],
                  "scopes": ["read", "publish"],
                  "namespaces": ["Contoso."]
                }
              ]
            }
            """;

        var options = AuthenticationCliOptions.Parse(
            ["start", "--identity-config-env", "SERVER_IDENTITIES"],
            name => name == "SERVER_IDENTITIES" ? json : null);

        Assert.Equal(AuthenticationProfile.Production, options.Configuration.Profile);
        Assert.True(options.Configuration.ProductionSecurity!
            .TryAuthenticateApiKey("rotated-key", out var identity));
        Assert.Equal("publisher", identity!.Name);
    }

    [Fact]
    public void Production_identity_json_reads_the_complete_standard_input()
    {
        var options = AuthenticationCliOptions.Parse(
            ["start", "--identity-config-stdin"],
            _ => null,
            standardInput: new StringReader(
                """
                {
                  "identities": [
                    {
                      "name": "reader",
                      "apiKeys": ["key"],
                      "scopes": ["read"],
                      "namespaces": ["*"]
                    }
                  ]
                }
                """));

        Assert.Equal(AuthenticationProfile.Production, options.Configuration.Profile);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"identities":[{"name":"broken","apiKeys":["key"]}]}""")]
    public void Incomplete_production_identity_json_is_a_configuration_error(string json)
    {
        Assert.Throws<CliConfigurationException>(() =>
            AuthenticationCliOptions.Parse(
                ["start", "--identity-config-env", "IDENTITIES"],
                name => name == "IDENTITIES" ? json : null));
    }
}
