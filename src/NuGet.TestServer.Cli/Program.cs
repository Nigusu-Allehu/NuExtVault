using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using NuGet.TestServer.Cli;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Storage;
using NuGet.TestServer.Vulnerabilities;

var arguments = args.ToList();
if (arguments.Count == 0 || !string.Equals(arguments[0], "start", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        "Usage: nuget-test-server start [--production] [--port <port>] [--data <directory>] [--storage <directory>] [package limit options] [authentication options]");
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

var vulnerabilityCache = new VulnerabilitySnapshotCache(
    Path.Combine(storageDirectory, "vulnerabilities"));
var vulnerabilitySnapshot = await vulnerabilityCache.LoadBestAsync(
    EmbeddedVulnerabilitySnapshot.Load());
var vulnerabilityProvider = new VulnerabilitySnapshotProvider(vulnerabilitySnapshot);
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

var mode = arguments.Any(argument =>
    string.Equals(argument, "--production", StringComparison.OrdinalIgnoreCase))
    ? ServerMode.Production
    : ServerMode.Test;
WebApplication app;
try
{
    app = ServerApplication.Build(
        url: $"http://127.0.0.1:{parsedPort}",
        storageDirectory: storageDirectory,
        authentication: authentication.Configuration,
        vulnerabilities: vulnerabilityProvider,
        mode: mode,
        packageLimits: packageLimits);
}
catch (Exception exception) when (
    exception is ServerHostingConfigurationException
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

var address = app.Services
    .GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()?
    .Addresses.Single()
    ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
Console.WriteLine($"Source:      {address}/v3/index.json");
Console.WriteLine($"Mode:        {mode}");
if (mode == ServerMode.Test)
{
    Console.WriteLine($"Control API: {address}/__test");
}

Console.WriteLine($"Health:      {address}/__test/health");
Console.WriteLine($"Storage:     {Path.GetFullPath(storageDirectory)}");
Console.WriteLine(
    $"Vulnerabilities: {vulnerabilityProvider.Active.UpdatedAt:O} ({vulnerabilityProvider.Active.Id})");

using var refreshClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};
var refresher = new VulnerabilitySnapshotRefresher(
    vulnerabilityProvider,
    vulnerabilityCache,
    refreshClient,
    new Uri("https://api.nuget.org/v3/vulnerabilities/index.json"));
var refreshTask =
    DateTimeOffset.UtcNow - vulnerabilityProvider.Active.FetchedAt > TimeSpan.FromHours(6)
        ? RefreshVulnerabilitiesAsync(refresher, app.Lifetime.ApplicationStopping)
        : Task.CompletedTask;

await app.WaitForShutdownAsync();
await refreshTask;
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

static async Task RefreshVulnerabilitiesAsync(
    VulnerabilitySnapshotRefresher refresher,
    CancellationToken token)
{
    try
    {
        if (await refresher.RefreshAsync(token))
        {
            Console.Error.WriteLine("Updated the cached nuget.org vulnerability snapshot.");
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
    }
    catch (VulnerabilityRefreshException exception)
    {
        Console.Error.WriteLine($"Warning: {exception.Message}");
    }
}
