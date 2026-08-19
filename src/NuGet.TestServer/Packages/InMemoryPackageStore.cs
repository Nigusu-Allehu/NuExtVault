using System.Collections.Concurrent;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed class InMemoryPackageStore : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TestPackage> _packages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _packagesDirectory;
    private readonly PackageTransferLimits _limits;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);

    public InMemoryPackageStore(
        string? storageDirectory = null,
        PackageTransferLimits? limits = null)
    {
        _limits = (limits ?? PackageTransferLimits.Default).Validate();
        if (storageDirectory is null)
        {
            return;
        }

        _packagesDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "packages");
        Directory.CreateDirectory(_packagesDirectory);
        LoadPersistedPackages();
    }

    public int Count => _packages.Count;

    public async ValueTask AddAsync(TestPackage package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await _persistenceGate.WaitAsync(token);
        try
        {
            var key = Key(package.Identity.Id, package.NormalizedVersion);
            if (!_packages.TryAdd(key, package))
            {
                throw new DuplicatePackageException(package.Identity.Id, package.NormalizedVersion);
            }

            if (_packagesDirectory is null)
            {
                return;
            }

            try
            {
                var persistedPath = await PersistPackageAsync(package, token);
                var persistedPackage = package.WithContentFile(persistedPath, ownsPath: false);
                if (!_packages.TryUpdate(
                        key,
                        persistedPackage,
                        package))
                {
                    throw new InvalidOperationException(
                        "The package changed while it was being persisted.");
                }

                package.Dispose();
            }
            catch
            {
                _packages.TryRemove(key, out _);
                package.Dispose();
                throw;
            }
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_packages.GetValueOrDefault(Key(id, Normalize(version))));
    }

    public ValueTask<IReadOnlyList<TestPackage>> FindByIdAsync(
        string id,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<TestPackage> result = _packages.Values
            .Where(package => string.Equals(package.Identity.Id, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(package => package.Identity.Version)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<TestPackage>> GetAllAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<TestPackage> result = _packages.Values
            .OrderBy(package => package.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Identity.Version)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<PackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        query ??= string.Empty;
        take = Math.Clamp(take, 0, 1000);
        skip = Math.Max(skip, 0);

        var applicablePackages = _packages.Values
            .Where(package => package.IsListed)
            .Where(package => includePrerelease || !package.Identity.Version.IsPrerelease)
            .ToArray();
        var matches = applicablePackages
            .GroupBy(package => package.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any(package =>
                package.Identity.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(group => new PackageSearchItem(
                group.MaxBy(package => package.Identity.Version)!,
                group.OrderBy(package => package.Identity.Version).ToArray()))
            .OrderBy(item => item.Package.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Package.Identity.Id, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<PackageSearchItem> items = matches
            .Skip(skip)
            .Take(take)
            .ToArray();
        return ValueTask.FromResult(new PackageSearchPage(matches.Length, items));
    }

    public async ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var key = Key(id, Normalize(version));
        await _persistenceGate.WaitAsync(token);
        try
        {
            while (_packages.TryGetValue(key, out var package))
            {
                if (_packagesDirectory is not null)
                {
                    SetUnlistedMarker(package, listed);
                }

                if (_packages.TryUpdate(key, package with { IsListed = listed }, package))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        await _persistenceGate.WaitAsync(token);
        var key = Key(id, Normalize(version));
        try
        {
            if (!_packages.TryGetValue(key, out var package))
            {
                return false;
            }

            if (_packagesDirectory is not null)
            {
                var packageDirectory = GetPackageDirectory(package);
                if (Directory.Exists(packageDirectory))
                {
                    Directory.Delete(packageDirectory, recursive: true);
                }

                _packages.TryRemove(key, out _);
                package.Dispose();
                return true;
            }

            if (!_packages.TryRemove(key, out package))
            {
                return false;
            }

            package.Dispose();
            return true;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public async ValueTask ResetAsync(CancellationToken token = default)
    {
        await _persistenceGate.WaitAsync(token);
        try
        {
            if (_packagesDirectory is not null)
            {
                if (Directory.Exists(_packagesDirectory))
                {
                    Directory.Delete(_packagesDirectory, recursive: true);
                }

                Directory.CreateDirectory(_packagesDirectory);
            }

            DisposePackages();
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _persistenceGate.WaitAsync();
        try
        {
            DisposePackages();
        }
        finally
        {
            _persistenceGate.Release();
            _persistenceGate.Dispose();
        }
    }

    private static string Key(string id, string version) =>
        $"{id.ToLowerInvariant()}\n{version.ToLowerInvariant()}";

    private static string Normalize(string version)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            return version.ToLowerInvariant();
        }

        return TestPackage.NormalizeVersion(parsed);
    }

    private void LoadPersistedPackages()
    {
        foreach (var packagePath in Directory.EnumerateFiles(
                     _packagesDirectory!,
                     "*.nupkg",
                     SearchOption.AllDirectories))
        {
            var package = TestPackage.FromFile(packagePath, _limits);
            var markerPath = GetUnlistedMarkerPath(package);
            package = package with { IsListed = !File.Exists(markerPath) };
            if (!_packages.TryAdd(Key(package.Identity.Id, package.NormalizedVersion), package))
            {
                throw new InvalidDataException(
                    $"Storage contains duplicate package '{package.Identity.Id} {package.NormalizedVersion}'.");
            }
        }
    }

    private async Task<string> PersistPackageAsync(TestPackage package, CancellationToken token)
    {
        var packageDirectory = GetPackageDirectory(package);
        Directory.CreateDirectory(packageDirectory);
        var destination = GetPackagePath(package);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = package.OpenReadStream())
            await using (var destinationStream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destinationStream, token);
            }

            File.Move(temporary, destination, overwrite: false);
            SetUnlistedMarker(package, package.IsListed);
            return destination;
        }
        catch
        {
            if (Directory.Exists(packageDirectory))
            {
                Directory.Delete(packageDirectory, recursive: true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void SetUnlistedMarker(TestPackage package, bool listed)
    {
        var markerPath = GetUnlistedMarkerPath(package);
        if (listed)
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        else
        {
            File.WriteAllText(markerPath, string.Empty);
        }
    }

    private string GetPackageDirectory(TestPackage package) =>
        Path.Combine(
            _packagesDirectory!,
            package.Identity.Id.ToLowerInvariant(),
            package.NormalizedVersion);

    private string GetPackagePath(TestPackage package)
    {
        var id = package.Identity.Id.ToLowerInvariant();
        return Path.Combine(
            GetPackageDirectory(package),
            $"{id}.{package.NormalizedVersion}.nupkg");
    }

    private string GetUnlistedMarkerPath(TestPackage package) =>
        Path.Combine(GetPackageDirectory(package), ".unlisted");

    private void DisposePackages()
    {
        foreach (var package in _packages.Values)
        {
            package.Dispose();
        }

        _packages.Clear();
    }
}

public sealed record PackageSearchPage(
    int TotalHits,
    IReadOnlyList<PackageSearchItem> Items);

public sealed record PackageSearchItem(
    TestPackage Package,
    IReadOnlyList<TestPackage> Versions);
