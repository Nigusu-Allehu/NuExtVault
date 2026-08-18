using System.Collections.Concurrent;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed class InMemoryPackageStore
{
    private readonly ConcurrentDictionary<string, TestPackage> _packages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _packagesDirectory;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);

    public InMemoryPackageStore(string? storageDirectory = null)
    {
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
        token.ThrowIfCancellationRequested();
        if (!_packages.TryAdd(Key(package.Identity.Id, package.NormalizedVersion), package))
        {
            throw new DuplicatePackageException(package.Identity.Id, package.NormalizedVersion);
        }

        if (_packagesDirectory is null)
        {
            return;
        }

        try
        {
            await _persistenceGate.WaitAsync(token);
            try
            {
                await PersistPackageAsync(package, token);
            }
            finally
            {
                _persistenceGate.Release();
            }
        }
        catch
        {
            _packages.TryRemove(Key(package.Identity.Id, package.NormalizedVersion), out _);
            throw;
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

    public ValueTask<IReadOnlyList<TestPackage>> SearchAsync(
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

        IReadOnlyList<TestPackage> result = _packages.Values
            .Where(package => package.IsListed)
            .Where(package => includePrerelease || !package.Identity.Version.IsPrerelease)
            .Where(package =>
                package.Identity.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Tags.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(package => package.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(package => package.Identity.Version)!)
            .OrderBy(package => package.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Skip(skip)
            .Take(take)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public async ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var key = Key(id, Normalize(version));
        if (_packagesDirectory is not null)
        {
            await _persistenceGate.WaitAsync(token);
        }

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
            if (_packagesDirectory is not null)
            {
                _persistenceGate.Release();
            }
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var key = Key(id, Normalize(version));
        if (!_packages.TryGetValue(key, out var package))
        {
            return false;
        }

        if (_packagesDirectory is not null)
        {
            await _persistenceGate.WaitAsync(token);
            try
            {
                var packageDirectory = GetPackageDirectory(package);
                if (Directory.Exists(packageDirectory))
                {
                    Directory.Delete(packageDirectory, recursive: true);
                }

                _packages.TryRemove(key, out _);
                return true;
            }
            finally
            {
                _persistenceGate.Release();
            }
        }

        return _packages.TryRemove(key, out _);
    }

    public async ValueTask ResetAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_packagesDirectory is not null)
        {
            await _persistenceGate.WaitAsync(token);
            try
            {
                if (Directory.Exists(_packagesDirectory))
                {
                    Directory.Delete(_packagesDirectory, recursive: true);
                }

                Directory.CreateDirectory(_packagesDirectory);
                _packages.Clear();
                return;
            }
            finally
            {
                _persistenceGate.Release();
            }
        }

        _packages.Clear();
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
            var package = TestPackage.FromContent(File.ReadAllBytes(packagePath));
            var markerPath = GetUnlistedMarkerPath(package);
            package = package with { IsListed = !File.Exists(markerPath) };
            if (!_packages.TryAdd(Key(package.Identity.Id, package.NormalizedVersion), package))
            {
                throw new InvalidDataException(
                    $"Storage contains duplicate package '{package.Identity.Id} {package.NormalizedVersion}'.");
            }
        }
    }

    private async Task PersistPackageAsync(TestPackage package, CancellationToken token)
    {
        var packageDirectory = GetPackageDirectory(package);
        Directory.CreateDirectory(packageDirectory);
        var destination = GetPackagePath(package);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, package.Content, token);
            File.Move(temporary, destination, overwrite: false);
            SetUnlistedMarker(package, package.IsListed);
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
}
