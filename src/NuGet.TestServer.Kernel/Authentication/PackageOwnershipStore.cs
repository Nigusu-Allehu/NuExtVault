using System.Text.Json;

namespace NuGet.TestServer.Authentication;

public interface IPackageOwnershipStore
{
    string? GetOwner(string packageId);
    ValueTask<PackagePublishResult> PublishAsync(
        string packageId,
        string identity,
        bool administrator,
        Func<CancellationToken, ValueTask<bool>> hasExistingPackages,
        Func<CancellationToken, ValueTask> publish,
        CancellationToken token);
}

public sealed record PackagePublishResult(bool Authorized, bool OwnershipClaimed);

public sealed class PackageOwnershipStore : IPackageOwnershipStore
{
    private readonly Dictionary<string, string> _owners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _filePath;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    public PackageOwnershipStore(string? storageDirectory)
    {
        if (storageDirectory is null)
        {
            return;
        }

        var securityDirectory = Path.Combine(storageDirectory, "security");
        Directory.CreateDirectory(securityDirectory);
        _filePath = Path.Combine(securityDirectory, "package-owners.json");
        if (File.Exists(_filePath))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_filePath));
            if (saved is not null)
            {
                foreach (var pair in saved)
                {
                    _owners.Add(pair.Key, pair.Value);
                }
            }
        }
    }

    public string? GetOwner(string packageId)
    {
        lock (_lock)
        {
            return _owners.GetValueOrDefault(packageId);
        }
    }

    public async ValueTask<PackagePublishResult> PublishAsync(
        string packageId,
        string identity,
        bool administrator,
        Func<CancellationToken, ValueTask<bool>> hasExistingPackages,
        Func<CancellationToken, ValueTask> publish,
        CancellationToken token)
    {
        await _publishLock.WaitAsync(token);
        try
        {
            string? owner;
            lock (_lock)
            {
                owner = _owners.GetValueOrDefault(packageId);
            }

            if (owner is not null &&
                !string.Equals(owner, identity, StringComparison.Ordinal) &&
                !administrator)
            {
                return new PackagePublishResult(false, false);
            }

            if (owner is null &&
                !administrator &&
                await hasExistingPackages(token))
            {
                return new PackagePublishResult(false, false);
            }

            await publish(token);
            if (owner is not null)
            {
                return new PackagePublishResult(true, false);
            }

            lock (_lock)
            {
                _owners.Add(packageId, identity);
                Persist();
            }

            return new PackagePublishResult(true, true);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private void Persist()
    {
        if (_filePath is null)
        {
            return;
        }

        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_owners));
        File.Move(temporary, _filePath, overwrite: true);
    }
}
