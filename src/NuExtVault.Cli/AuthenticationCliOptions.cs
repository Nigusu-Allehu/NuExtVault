using NuExtVault.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuExtVault.Cli;

public sealed record AuthenticationCliOptions(
    AuthenticationConfiguration Configuration,
    IReadOnlyList<string> Warnings,
    string? GeneratedApiKey)
{
    public static AuthenticationCliOptions Parse(
        IReadOnlyList<string> arguments,
        Func<string, string?> getEnvironmentVariable,
        Func<string>? generateApiKey = null,
        TextReader? standardInput = null,
        Func<string>? promptForPassword = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var warnings = new List<string>();
        var username = ReadOption(arguments, "--username");
        var password = ResolveSecret(
            arguments,
            literalOption: "--password",
            environmentOption: "--password-env",
            stdinOption: "--password-stdin",
            getEnvironmentVariable,
            standardInput,
            warnings);
        var apiKey = ResolveSecret(
            arguments,
            literalOption: "--api-key",
            environmentOption: "--api-key-env",
            stdinOption: "--api-key-stdin",
            getEnvironmentVariable,
            standardInput,
            warnings);
        var identityJson = ResolveSecret(
            arguments,
            literalOption: "--identity-config",
            environmentOption: "--identity-config-env",
            stdinOption: "--identity-config-stdin",
            getEnvironmentVariable,
            standardInput,
            warnings,
            readEntireStandardInput: true);

        string? generatedApiKey = null;
        if (HasFlag(arguments, "--generate-api-key"))
        {
            if (apiKey is not null)
            {
                throw new CliConfigurationException(
                    "--generate-api-key cannot be combined with another API-key source.");
            }

            generatedApiKey = (generateApiKey ?? throw new CliConfigurationException(
                "API-key generation is unavailable."))();
            if (string.IsNullOrEmpty(generatedApiKey))
            {
                throw new CliConfigurationException("Generated API key cannot be empty.");
            }

            apiKey = generatedApiKey;
        }

        if (username is not null && password is null && promptForPassword is not null)
        {
            password = promptForPassword();
        }

        try
        {
            if (identityJson is not null)
            {
                if (username is not null || password is not null || apiKey is not null)
                {
                    throw new CliConfigurationException(
                        "Production identity configuration cannot be combined with legacy credentials.");
                }

                var document = JsonSerializer.Deserialize<ProductionIdentityDocument>(
                    identityJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new JsonStringEnumConverter() }
                    }) ?? throw new CliConfigurationException(
                        "Production identity configuration is empty.");
                if (document.Identities is null)
                {
                    throw new CliConfigurationException(
                        "Production identity configuration must contain an identities array.");
                }

                return new AuthenticationCliOptions(
                    AuthenticationConfiguration.CreateProduction(
                        ProductionSecurityConfiguration.Create(document.Identities)),
                    warnings,
                    null);
            }

            var configuration = AuthenticationConfiguration.Create(username, password, apiKey);
            return new AuthenticationCliOptions(configuration, warnings, generatedApiKey);
        }
        catch (AuthenticationConfigurationException exception)
        {
            throw new CliConfigurationException(exception.Message, exception);
        }
        catch (JsonException exception)
        {
            throw new CliConfigurationException(
                "Production identity configuration is not valid JSON.",
                exception);
        }
    }

    private sealed record ProductionIdentityDocument(
        IReadOnlyList<ProductionIdentityOptions>? Identities);

    private static string? ResolveSecret(
        IReadOnlyList<string> arguments,
        string literalOption,
        string environmentOption,
        string stdinOption,
        Func<string, string?> getEnvironmentVariable,
        TextReader? standardInput,
        ICollection<string> warnings,
        bool readEntireStandardInput = false)
    {
        var literal = ReadOption(arguments, literalOption);
        var environmentName = ReadOption(arguments, environmentOption);
        var fromStandardInput = HasFlag(arguments, stdinOption);
        var sourceCount = (literal is not null ? 1 : 0) +
                          (environmentName is not null ? 1 : 0) +
                          (fromStandardInput ? 1 : 0);
        if (sourceCount > 1)
        {
            throw new CliConfigurationException(
                $"Only one of {literalOption}, {environmentOption}, and {stdinOption} may be used.");
        }

        if (literal is not null)
        {
            warnings.Add(
                $"{literalOption} may expose a secret through process listings or logs; prefer {environmentOption}.");
            return literal;
        }

        if (environmentName is not null)
        {
            var value = getEnvironmentVariable(environmentName);
            return string.IsNullOrEmpty(value)
                ? throw new CliConfigurationException(
                    $"Environment variable '{environmentName}' is missing or empty.")
                : value;
        }

        if (fromStandardInput)
        {
            if (standardInput is null)
            {
                throw new CliConfigurationException(
                    $"{stdinOption} requires standard input.");
            }

            var value = readEntireStandardInput
                ? standardInput.ReadToEnd()
                : standardInput.ReadLine();
            return string.IsNullOrEmpty(value)
                ? throw new CliConfigurationException(
                    $"{stdinOption} received an empty secret.")
                : value;
        }

        return null;
    }

    private static string? ReadOption(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 1; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--"))
            {
                throw new CliConfigurationException($"{name} requires a value.");
            }

            return arguments[index + 1];
        }

        return null;
    }

    private static bool HasFlag(IReadOnlyList<string> arguments, string name) =>
        arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class CliConfigurationException(
    string message,
    Exception? innerException = null)
    : Exception(message, innerException);
