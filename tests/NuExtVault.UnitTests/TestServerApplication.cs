using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NuExtVault.Authentication;
using NuExtVault.Hosting;
using NuExtVault.Kernel;

namespace NuExtVault.UnitTests;

/// <summary>
/// Builds a composed server application without starting a listener so kernel
/// composition can be inspected directly.
/// </summary>
internal sealed class TestServerApplication : IDisposable
{
    private readonly WebApplication _application;
    private readonly TemporaryDirectory? _storage;

    private TestServerApplication(WebApplication application, TemporaryDirectory? storage)
    {
        _application = application;
        _storage = storage;
    }

    public IServiceProvider Services => _application.Services;

    public WebApplication Application => _application;

    public OperationRegistry Registry => _application.Services.GetRequiredService<OperationRegistry>();

    public ResolvedExtensionGraph Graph =>
        _application.Services.GetRequiredService<ResolvedExtensionGraph>();

    public static TestServerApplication Build(ServerProfile profile)
    {
        var storage = profile.Kind == ServerProfileKind.Embedded ? null : new TemporaryDirectory();
        try
        {
            var composition = ServerComposition.Create(
                profile,
                storageDirectory: storage?.Path,
                authentication: AuthenticationConfiguration.Anonymous);
            return new TestServerApplication(ServerApplication.Build(composition), storage);
        }
        catch
        {
            storage?.Dispose();
            throw;
        }
    }

    public static TestServerApplication BuildProduction()
    {
        var storage = new TemporaryDirectory();
        try
        {
            var security = ProductionSecurityConfiguration.Create(
            [
                new("publisher", ["publisher-key"], [SecurityScope.Read, SecurityScope.Publish], ["*"])
            ]);
            var composition = ServerComposition.Create(
                ServerProfiles.Production,
                storageDirectory: storage.Path,
                authentication: AuthenticationConfiguration.CreateProduction(security),
                trustedProxies: new TrustedProxyOptions(["127.0.0.1"]),
                supplyChain: new Packages.SupplyChainOptions());
            return new TestServerApplication(ServerApplication.Build(composition), storage);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _application.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _storage?.Dispose();
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NuExtVault.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
