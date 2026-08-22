using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Cli;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Extensions.Vulnerabilities;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Storage;

var arguments = args.ToList();
if (arguments.Count == 0)
{
    Console.Error.WriteLine(
        "Usage: nuget-test-server <start|backup|restore> [options]; start supports [--production] [--port <port>] [--data <directory>] [--storage <directory>] [--extension-root <directory>] [--extension-trust-root <json-file>] [--extension-grant <capability>] [package limit options] [authentication options]");
    return 2;
}

if (string.Equals(arguments[0], "backup", StringComparison.OrdinalIgnoreCase))
{
    var backupStorage = ReadOption(arguments, "--storage") ?? LocalStoragePaths.DefaultRoot;
    var output = ReadOption(arguments, "--output");
    if (output is null)
    {
        Console.Error.WriteLine("backup requires --output <archive.zip>.");
        return 2;
    }

    try
    {
        var manifest = await StorageBackup.CreateAsync(backupStorage, output);
        Console.WriteLine(
            $"Created backup '{Path.GetFullPath(output)}' with {manifest.Files.Count} files.");
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"Backup failed: {exception.Message}");
        return 1;
    }
}

if (string.Equals(arguments[0], "restore", StringComparison.OrdinalIgnoreCase))
{
    var restoreStorage = ReadOption(arguments, "--storage") ?? LocalStoragePaths.DefaultRoot;
    var input = ReadOption(arguments, "--input");
    if (input is null)
    {
        Console.Error.WriteLine("restore requires --input <archive.zip>.");
        return 2;
    }

    try
    {
        using var externalRuntime = ExternalExtensionPackageLoader.Load(
            new ExternalExtensionConfiguration(
                [.. ReadRepeatedPathOption(arguments, "--extension-root")],
                ReadTrustRoots(arguments),
                TimeProvider.System));
        if (externalRuntime.Diagnostics.Results.FirstOrDefault(result => !result.Succeeded)
            is { } failure)
        {
            throw new CliConfigurationException(
                $"{failure.FailureCode}: {failure.RedactedMessage}");
        }

        var participants = externalRuntime.Modules
            .Select(module => module.Contribution.Manifest)
            .Where(manifest => manifest.State is not null)
            .Select(manifest => new StateParticipantDescriptor(
                manifest.Identity.Id,
                manifest.Identity.Version,
                manifest.State!.SchemaName,
                manifest.State.SchemaVersion,
                manifest.State.Required).Validate());
        var manifest = await StorageBackup.RestoreAsync(
            input,
            restoreStorage,
            [.. KernelStateParticipants.BuiltIn, .. participants],
            CancellationToken.None);
        Console.WriteLine(
            $"Restored {manifest.Files.Count} files into '{Path.GetFullPath(restoreStorage)}'.");
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or CliConfigurationException)
    {
        Console.Error.WriteLine($"Restore failed: {exception.Message}");
        return 1;
    }
}

if (!string.Equals(arguments[0], "start", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Unknown command '{arguments[0]}'.");
    return 2;
}

var port = ReadOption(arguments, "--port") ?? "0";
if (!int.TryParse(port, out var parsedPort) || parsedPort is < 0 or > 65535)
{
    Console.Error.WriteLine("--port must be between 0 and 65535.");
    return 2;
}

var storageDirectory = ReadOption(arguments, "--storage") ?? LocalStoragePaths.DefaultRoot;
PackageTransferLimits packageLimits;
try
{
    packageLimits = new PackageTransferLimits
    {
        MaxRequestBodyBytes = ReadPositiveLongOption(
            arguments,
            "--max-request-bytes",
            PackageTransferLimits.DefaultMaxRequestBodyBytes),
        MaxPackageBytes = ReadPositiveLongOption(
            arguments,
            "--max-package-bytes",
            PackageTransferLimits.DefaultMaxPackageBytes),
        MaxArchiveEntries = checked((int)ReadPositiveLongOption(
            arguments,
            "--max-archive-entries",
            PackageTransferLimits.DefaultMaxArchiveEntries)),
        MaxArchiveEntryBytes = ReadPositiveLongOption(
            arguments,
            "--max-entry-bytes",
            PackageTransferLimits.DefaultMaxArchiveEntryBytes),
        MaxExpandedArchiveBytes = ReadPositiveLongOption(
            arguments,
            "--max-expanded-bytes",
            PackageTransferLimits.DefaultMaxExpandedArchiveBytes),
        TemporaryDirectory = Path.Combine(storageDirectory, "tmp")
    }.Validate();
}
catch (Exception exception) when (
    exception is ArgumentException or OverflowException or CliConfigurationException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

AuthenticationCliOptions authentication;
try
{
    authentication = AuthenticationCliOptions.Parse(
        arguments,
        Environment.GetEnvironmentVariable,
        GenerateApiKey,
        Console.In,
        Console.IsInputRedirected ? null : () => ReadSecret("Password: "));
}
catch (CliConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

foreach (var warning in authentication.Warnings)
{
    Console.Error.WriteLine($"Warning: {warning}");
}

if (authentication.GeneratedApiKey is not null)
{
    Console.Error.WriteLine($"Generated API key: {authentication.GeneratedApiKey}");
    authentication = authentication with { GeneratedApiKey = null };
}

var production = arguments.Any(argument =>
    string.Equals(argument, "--production", StringComparison.OrdinalIgnoreCase));
ServerComposition composition;
WebApplication app;
try
{
    composition = CliServerProfileFactory.Create(
        production,
        url: ReadOption(arguments, "--url") ?? $"http://127.0.0.1:{parsedPort}",
        storageDirectory: storageDirectory,
        authentication: authentication.Configuration,
        packageLimits: packageLimits,
        trustedProxies: ParseTrustedProxies(arguments),
        extensionRoots: ReadRepeatedPathOption(arguments, "--extension-root"),
        extensionTrustRoots: ReadTrustRoots(arguments),
        extensionGrants: ReadExtensionGrants(arguments));
    app = ServerApplication.Build(composition);
}
catch (Exception exception) when (
    exception is ServerHostingConfigurationException
        or CliConfigurationException
        or PackageStorageInUseException
        or PackageStorageCorruptionException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
await app.StartAsync();

var dataDirectory = ReadOption(arguments, "--data");
if (dataDirectory is not null)
{
    if (!Directory.Exists(dataDirectory))
    {
        Console.Error.WriteLine($"Data directory '{dataDirectory}' does not exist.");
        await app.DisposeAsync();
        return 2;
    }

    var store = app.Services.GetRequiredService<IPackageStore>();
    foreach (var packagePath in Directory.EnumerateFiles(dataDirectory, "*.nupkg"))
    {
        await using var packageStream = File.OpenRead(packagePath);
        var package = await TestPackage.FromStreamAsync(packageStream, packageLimits);
        if (await store.FindAsync(package.Identity.Id, package.NormalizedVersion) is null)
        {
            await store.AddAsync(package);
        }
        else
        {
            package.Dispose();
        }
    }
}

var addresses = app.Services
    .GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()?
    .Addresses.ToArray()
    ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
if (addresses.Length == 0)
{
    throw new InvalidOperationException("Kestrel did not publish a listening address.");
}

var address = addresses[0];
Console.WriteLine($"Source:      {address}/v3/index.json");
foreach (var additionalAddress in addresses.Skip(1))
{
    Console.WriteLine($"Source:      {additionalAddress}/v3/index.json");
}
Console.WriteLine($"Mode:        {composition.Hosting.Mode}");
if (composition.Hosting.Mode == ServerMode.Test)
{
    Console.WriteLine($"Control API: {address}/__test");
}

Console.WriteLine($"Health:      {address}/__test/health");
Console.WriteLine($"Liveness:    {address}/health/live");
Console.WriteLine($"Readiness:   {address}/health/ready");
Console.WriteLine($"Storage:     {Path.GetFullPath(storageDirectory)}");
var vulnerabilityStatus = app.Services.GetRequiredService<VulnerabilityExtension>().Health;
Console.WriteLine(
    $"Vulnerabilities: {vulnerabilityStatus.UpdatedAt:O} ({vulnerabilityStatus.SnapshotId})");

await app.WaitForShutdownAsync();
await app.DisposeAsync();
return 0;

static string? ReadOption(IReadOnlyList<string> arguments, string name)
{
    for (var index = 1; index < arguments.Count - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }

    }

    return null;
}

static ImmutableArray<string> ReadRepeatedPathOption(
    IReadOnlyList<string> arguments,
    string name)
{
    var values = ImmutableArray.CreateBuilder<string>();
    for (var index = 1; index < arguments.Count; index++)
    {
        if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliConfigurationException($"{name} requires one directory path.");
        }
        values.Add(Path.GetFullPath(arguments[++index]));
    }
    return values.ToImmutable();
}

static ImmutableArray<string> ReadExtensionGrants(IReadOnlyList<string> arguments)
{
    var grants = ImmutableArray.CreateBuilder<string>();
    for (var index = 1; index < arguments.Count; index++)
    {
        if (!string.Equals(
                arguments[index],
                "--extension-grant",
                StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliConfigurationException(
                "--extension-grant requires one capability name.");
        }

        grants.Add(arguments[++index]);
    }

    return grants.ToImmutable();
}

static ImmutableArray<ConformanceTrustRoot> ReadTrustRoots(IReadOnlyList<string> arguments)
{
    var roots = ImmutableArray.CreateBuilder<ConformanceTrustRoot>();
    foreach (var path in ReadRepeatedPathOption(arguments, "--extension-trust-root"))
    {
        if (!File.Exists(path))
        {
            throw new CliConfigurationException(
                $"Extension trust-root file '{Path.GetFileName(path)}' does not exist.");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            roots.Add(new ConformanceTrustRoot(
                root.GetProperty("publisher").GetString()!,
                root.GetProperty("keyId").GetString()!,
                root.GetProperty("algorithm").GetString()!,
                Convert.FromBase64String(root.GetProperty("subjectPublicKeyInfoBase64").GetString()!)));
        }
        catch (Exception exception) when (
            exception is JsonException or
                FormatException or
                InvalidOperationException or
                KeyNotFoundException)
        {
            throw new CliConfigurationException(
                $"Extension trust-root file '{Path.GetFileName(path)}' is invalid.");
        }
    }
    return roots.ToImmutable();
}

static TrustedProxyOptions? ParseTrustedProxies(IReadOnlyList<string> arguments)
{
    var value = ReadOption(arguments, "--trusted-proxy");
    return value is null
        ? null
        : new TrustedProxyOptions(
            value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

static long ReadPositiveLongOption(
    IReadOnlyList<string> arguments,
    string name,
    long defaultValue)
{
    var value = ReadOption(arguments, name);
    if (value is null)
    {
        return defaultValue;
    }

    if (!long.TryParse(value, out var parsed) || parsed <= 0)
    {
        throw new CliConfigurationException($"{name} must be a positive integer.");
    }

    return parsed;
}

static string GenerateApiKey()
{
    return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return new string(characters.ToArray());
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            characters.Add(key.KeyChar);
        }
    }
}
