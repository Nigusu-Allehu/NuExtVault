using NuGet.TestServer.Authentication;
using NuGet.TestServer.Cli;

namespace NuGet.TestServer.FunctionalTests;

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
}
