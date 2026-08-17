using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using NuGet.TestServer.Cli;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Storage;

var arguments = args.ToList();
if (arguments.Count == 0 || !string.Equals(arguments[0], "start", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        "Usage: nuget-test-server start [--port <port>] [--data <directory>] [--storage <directory>] [authentication options]");
    return 2;
}

var port = ReadOption(arguments, "--port") ?? "0";
if (!int.TryParse(port, out var parsedPort) || parsedPort is < 0 or > 65535)
{
    Console.Error.WriteLine("--port must be between 0 and 65535.");
    return 2;
}

var storageDirectory = ReadOption(arguments, "--storage") ?? LocalStoragePaths.DefaultRoot;
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

var app = ServerApplication.Build(
    url: $"http://127.0.0.1:{parsedPort}",
    storageDirectory: storageDirectory,
    authentication: authentication.Configuration);
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

    var store = app.Services.GetRequiredService<InMemoryPackageStore>();
    foreach (var packagePath in Directory.EnumerateFiles(dataDirectory, "*.nupkg"))
    {
        var package = TestPackage.FromContent(await File.ReadAllBytesAsync(packagePath));
        if (await store.FindAsync(package.Identity.Id, package.NormalizedVersion) is null)
        {
            await store.AddAsync(package);
        }
    }
}

var address = app.Services
    .GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()?
    .Addresses.Single()
    ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
Console.WriteLine($"Source:      {address}/v3/index.json");
Console.WriteLine($"Control API: {address}/__test");
Console.WriteLine($"Storage:     {Path.GetFullPath(storageDirectory)}");

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
