using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed class DurablePackageStore : IPackageStore
{
    private readonly string _root;
    private readonly FileStream _rootLease;
    private readonly FilePackageBlobStore _blobs;
    private readonly SqlitePackageMetadataStore _metadata;
    private readonly InMemoryPackageStore _cache;
    private readonly PackageTransferLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DurablePackageStore(
        string storageDirectory,
        PackageTransferLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _root = Path.GetFullPath(storageDirectory);
        _limits = (limits ?? PackageTransferLimits.Default).Validate();
        Directory.CreateDirectory(_root);
        _rootLease = AcquireRootLease(_root);
        try
        {
            _blobs = new FilePackageBlobStore(_root);
            _metadata = new SqlitePackageMetadataStore(Path.Combine(_root, "packages.db"));
            _cache = new InMemoryPackageStore(limits: _limits);
            RecoverPendingDeletes();
            ImportUntrackedBlobs();
            LoadAndVerifyPackages();
        }
        catch
        {
            _rootLease.Dispose();
            throw;
        }
    }

    public async ValueTask AddAsync(TestPackage package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await _gate.WaitAsync(token);
        StoredPackageBlob? blob = null;
        try
        {
            if (await _cache.FindAsync(package.Identity.Id, package.NormalizedVersion, token) is not null)
            {
                throw new DuplicatePackageException(package.Identity.Id, package.NormalizedVersion);
            }

            blob = await _blobs.PublishAsync(package, token);
            var metadata = PackageMetadata.FromPackage(package, blob);
            try
            {
                _metadata.Insert(metadata);
            }
            catch
            {
                _blobs.Delete(blob.RelativePath);
                throw;
            }

            var persisted = package.WithContentFile(blob.FullPath, ownsPath: false);
            try
            {
                await _cache.AddAsync(persisted, token);
                package.Dispose();
            }
            catch
            {
                _metadata.Delete(package.Identity.Id, package.NormalizedVersion);
                _blobs.Delete(blob.RelativePath);
                persisted.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token = default) =>
        _cache.FindAsync(id, version, token);

    public ValueTask<IReadOnlyList<TestPackage>> FindByIdAsync(
        string id,
        CancellationToken token = default) =>
        _cache.FindByIdAsync(id, token);

    public ValueTask<IReadOnlyList<TestPackage>> GetAllAsync(CancellationToken token = default) =>
        _cache.GetAllAsync(token);

    public ValueTask<IReadOnlyList<TestPackage>> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        CancellationToken token = default) =>
        _cache.SearchAsync(query, includePrerelease, skip, take, token);

    public async ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var normalizedVersion = Normalize(version);
            if (!_metadata.SetListed(id, normalizedVersion, listed))
            {
                return false;
            }

            if (!await _cache.SetListedAsync(id, normalizedVersion, listed, token))
            {
                throw new PackageStorageCorruptionException(
                    $"Metadata exists for package '{id} {normalizedVersion}', but it is absent from memory.");
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var normalizedVersion = Normalize(version);
            var metadata = _metadata.Find(id, normalizedVersion);
            if (metadata is null)
            {
                return false;
            }

            var pendingDelete = _blobs.StageDelete(metadata.BlobPath);
            try
            {
                if (!_metadata.Delete(id, normalizedVersion))
                {
                    _blobs.RollbackDelete(pendingDelete, metadata.BlobPath);
                    return false;
                }
            }
            catch
            {
                _blobs.RollbackDelete(pendingDelete, metadata.BlobPath);
                throw;
            }

            if (!await _cache.DeleteAsync(id, normalizedVersion, token))
            {
                throw new PackageStorageCorruptionException(
                    $"Deleted metadata for package '{id} {normalizedVersion}', but it was absent from memory.");
            }

            _blobs.CompleteDelete(pendingDelete);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResetAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        var pendingDeletes = new List<(string PendingPath, string BlobPath)>();
        try
        {
            try
            {
                foreach (var metadata in _metadata.GetAll())
                {
                    token.ThrowIfCancellationRequested();
                    pendingDeletes.Add((
                        _blobs.StageDelete(metadata.BlobPath),
                        metadata.BlobPath));
                }

                token.ThrowIfCancellationRequested();
                _metadata.Clear();
            }
            catch
            {
                foreach (var pendingDelete in pendingDeletes.AsEnumerable().Reverse())
                {
                    _blobs.RollbackDelete(
                        pendingDelete.PendingPath,
                        pendingDelete.BlobPath);
                }

                throw;
            }

            await _cache.ResetAsync(CancellationToken.None);
            foreach (var pendingDelete in pendingDeletes)
            {
                _blobs.CompleteDelete(pendingDelete.PendingPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _cache.DisposeAsync();
            _rootLease.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static FileStream AcquireRootLease(string root)
    {
        try
        {
            return new FileStream(
                Path.Combine(root, ".storage.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new PackageStorageInUseException(
                $"Package storage root '{root}' is already in use by another server process.",
                exception);
        }
    }

    private void RecoverPendingDeletes()
    {
        foreach (var pendingPath in _blobs.GetPendingDeletes())
        {
            var blobPath = _blobs.GetBlobPathForPendingDelete(pendingPath);
            if (_metadata.ContainsBlob(blobPath))
            {
                _blobs.RollbackDelete(pendingPath, blobPath);
            }
            else
            {
                _blobs.CompleteDelete(pendingPath);
            }
        }
    }

    private void ImportUntrackedBlobs()
    {
        foreach (var blob in _blobs.GetPublishedBlobs())
        {
            if (_metadata.ContainsBlob(blob.RelativePath))
            {
                continue;
            }

            TestPackage package;
            try
            {
                package = TestPackage.FromFile(blob.FullPath, _limits);
            }
            catch (Exception exception) when (
                exception is InvalidPackageException or PackageLimitExceededException)
            {
                throw new PackageStorageCorruptionException(
                    $"Untracked package blob '{blob.RelativePath}' is invalid and cannot be recovered.",
                    exception);
            }

            var markerPath = Path.Combine(Path.GetDirectoryName(blob.FullPath)!, ".unlisted");
            package = package with { IsListed = !File.Exists(markerPath) };
            var withHash = blob with { Sha256 = ComputeSha256(blob.FullPath) };
            _metadata.Insert(PackageMetadata.FromPackage(package, withHash));
            package.Dispose();
        }
    }

    private void LoadAndVerifyPackages()
    {
        foreach (var metadata in _metadata.GetAll())
        {
            var fullPath = _blobs.GetFullPath(metadata.BlobPath);
            if (!File.Exists(fullPath))
            {
                throw Corruption(metadata, "blob is missing");
            }

            var actualHash = ComputeSha256(fullPath);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, metadata.Sha256))
            {
                throw Corruption(metadata, "blob SHA-256 does not match durable metadata");
            }

            TestPackage package;
            try
            {
                package = TestPackage.FromFile(fullPath, _limits) with
                {
                    IsListed = metadata.IsListed,
                    Published = metadata.Published
                };
            }
            catch (Exception exception) when (
                exception is InvalidPackageException or PackageLimitExceededException)
            {
                throw Corruption(metadata, "blob is not a valid bounded NuGet package", exception);
            }

            if (!string.Equals(
                    package.Identity.Id,
                    metadata.Id,
                    StringComparison.OrdinalIgnoreCase) ||
                package.NormalizedVersion != metadata.NormalizedVersion)
            {
                package.Dispose();
                throw Corruption(metadata, "blob identity does not match durable metadata");
            }

            _cache.AddAsync(package).AsTask().GetAwaiter().GetResult();
        }
    }

    private static PackageStorageCorruptionException Corruption(
        PackageMetadata metadata,
        string detail,
        Exception? innerException = null) =>
        new(
            $"Package storage is corrupt for '{metadata.Id} {metadata.NormalizedVersion}': {detail}.",
            innerException);

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static string Normalize(string version) =>
        NuGetVersion.TryParse(version, out var parsed)
            ? TestPackage.NormalizeVersion(parsed)
            : version.ToLowerInvariant();
}

public interface IPackageBlobStore
{
    ValueTask<StoredPackageBlob> PublishAsync(
        TestPackage package,
        CancellationToken token = default);

    void Delete(string relativePath);
}

public interface IPackageMetadataStore
{
    IReadOnlyList<PackageMetadata> GetAll();
    void Insert(PackageMetadata package);
    bool SetListed(string id, string normalizedVersion, bool listed);
    bool Delete(string id, string normalizedVersion);
}

public sealed record StoredPackageBlob(
    string RelativePath,
    string FullPath,
    long Length,
    byte[] Sha256);

public sealed record PackageMetadata(
    string Id,
    string NormalizedVersion,
    string OriginalVersion,
    string Description,
    string Authors,
    string Tags,
    byte[] Nuspec,
    DateTimeOffset Published,
    bool IsListed,
    long ContentLength,
    string BlobPath,
    byte[] Sha256)
{
    internal static PackageMetadata FromPackage(TestPackage package, StoredPackageBlob blob) =>
        new(
            package.Identity.Id,
            package.NormalizedVersion,
            package.Identity.Version.ToFullString(),
            package.Description,
            package.Authors,
            package.Tags,
            package.NuspecContent,
            package.Published,
            package.IsListed,
            blob.Length,
            blob.RelativePath,
            blob.Sha256);
}

internal sealed class FilePackageBlobStore : IPackageBlobStore
{
    private readonly string _root;
    private readonly string _packagesDirectory;
    private readonly string _trashDirectory;

    public FilePackageBlobStore(string root)
    {
        _root = root;
        _packagesDirectory = Path.Combine(root, "packages");
        _trashDirectory = Path.Combine(root, "trash");
        Directory.CreateDirectory(_packagesDirectory);
        Directory.CreateDirectory(_trashDirectory);
        CleanupIncompletePublications();
    }

    public async ValueTask<StoredPackageBlob> PublishAsync(
        TestPackage package,
        CancellationToken token = default)
    {
        var id = package.Identity.Id.ToLowerInvariant();
        var relativePath = Path.Combine(
            "packages",
            id,
            package.NormalizedVersion,
            $"{id}.{package.NormalizedVersion}.nupkg");
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            {
                await using var source = package.OpenReadStream();
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, token);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    length = checked(length + read);
                }

                await destination.FlushAsync(token);
                destination.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: false);
            return new StoredPackageBlob(relativePath, fullPath, length, hash.GetHashAndReset());
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete(string relativePath)
    {
        var path = GetFullPath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string StageDelete(string relativePath)
    {
        var source = GetFullPath(relativePath);
        var suffix = Path.GetRelativePath(_packagesDirectory, source);
        var destination = Path.Combine(_trashDirectory, suffix);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination, overwrite: false);
        return destination;
    }

    public void RollbackDelete(string pendingPath, string relativePath)
    {
        if (!File.Exists(pendingPath))
        {
            return;
        }

        var destination = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(pendingPath, destination, overwrite: false);
    }

    public void CompleteDelete(string pendingPath)
    {
        if (File.Exists(pendingPath))
        {
            File.Delete(pendingPath);
        }
    }

    public IEnumerable<string> GetPendingDeletes() =>
        Directory.EnumerateFiles(_trashDirectory, "*.nupkg", SearchOption.AllDirectories);

    public string GetBlobPathForPendingDelete(string pendingPath) =>
        Path.Combine("packages", Path.GetRelativePath(_trashDirectory, pendingPath));

    public IEnumerable<StoredPackageBlob> GetPublishedBlobs()
    {
        foreach (var path in Directory.EnumerateFiles(
                     _packagesDirectory,
                     "*.nupkg",
                     SearchOption.AllDirectories))
        {
            yield return new StoredPackageBlob(
                Path.GetRelativePath(_root, path),
                path,
                new FileInfo(path).Length,
                []);
        }
    }

    public string GetFullPath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageStorageCorruptionException(
                $"Package blob path '{relativePath}' escapes the storage root.");
        }

        return path;
    }

    private void CleanupIncompletePublications()
    {
        foreach (var path in Directory.EnumerateFiles(
                     _packagesDirectory,
                     "*.tmp",
                     SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
    }
}

internal sealed class SqlitePackageMetadataStore : IPackageMetadataStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;

    public SqlitePackageMetadataStore(string databasePath)
    {
        SqliteRuntime.Initialize();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        try
        {
            Migrate();
        }
        catch (SqliteException exception)
        {
            throw new PackageStorageCorruptionException(
                $"Package metadata database '{databasePath}' could not be opened or migrated.",
                exception);
        }
    }

    public IReadOnlyList<PackageMetadata> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, normalized_version, original_version, description, authors, tags,
                   nuspec, published_utc, is_listed, content_length, blob_path, sha256
            FROM packages
            ORDER BY id COLLATE NOCASE, normalized_version;
            """;
        using var reader = command.ExecuteReader();
        var packages = new List<PackageMetadata>();
        while (reader.Read())
        {
            packages.Add(Read(reader));
        }

        return packages;
    }

    public PackageMetadata? Find(string id, string normalizedVersion)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, normalized_version, original_version, description, authors, tags,
                   nuspec, published_utc, is_listed, content_length, blob_path, sha256
            FROM packages
            WHERE id = $id COLLATE NOCASE AND normalized_version = $version;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", normalizedVersion);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Insert(PackageMetadata package)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO packages (
                id, normalized_version, original_version, description, authors, tags,
                nuspec, published_utc, is_listed, content_length, blob_path, sha256)
            VALUES (
                $id, $normalizedVersion, $originalVersion, $description, $authors, $tags,
                $nuspec, $published, $listed, $length, $blobPath, $sha256);
            """;
        AddParameters(command, package);
        try
        {
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DuplicatePackageException(package.Id, package.NormalizedVersion);
        }
    }

    public bool SetListed(string id, string normalizedVersion, bool listed)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE packages
            SET is_listed = $listed
            WHERE id = $id COLLATE NOCASE AND normalized_version = $version;
            """;
        command.Parameters.AddWithValue("$listed", listed ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", normalizedVersion);
        var changed = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return changed;
    }

    public bool Delete(string id, string normalizedVersion)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM packages WHERE id = $id COLLATE NOCASE AND normalized_version = $version;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", normalizedVersion);
        var changed = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return changed;
    }

    public bool ContainsBlob(string blobPath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM packages WHERE blob_path = $path);";
        command.Parameters.AddWithValue("$path", blobPath);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public void Clear()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM packages;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void Migrate()
    {
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > CurrentSchemaVersion)
        {
            throw new PackageStorageCorruptionException(
                $"Package metadata schema version {version} is newer than supported version " +
                $"{CurrentSchemaVersion}.");
        }

        if (version == 0)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                CREATE TABLE packages (
                    id TEXT NOT NULL COLLATE NOCASE,
                    normalized_version TEXT NOT NULL,
                    original_version TEXT NOT NULL,
                    description TEXT NOT NULL,
                    authors TEXT NOT NULL,
                    tags TEXT NOT NULL,
                    nuspec BLOB NOT NULL,
                    published_utc TEXT NOT NULL,
                    is_listed INTEGER NOT NULL CHECK (is_listed IN (0, 1)),
                    content_length INTEGER NOT NULL CHECK (content_length >= 0),
                    blob_path TEXT NOT NULL UNIQUE,
                    sha256 BLOB NOT NULL CHECK (length(sha256) = 32),
                    PRIMARY KEY (id, normalized_version)
                );
                CREATE TABLE storage_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                INSERT INTO storage_migrations(version, applied_utc)
                VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static PackageMetadata Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (byte[])reader[6],
            DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt64(8) == 1,
            reader.GetInt64(9),
            reader.GetString(10),
            (byte[])reader[11]);

    private static void AddParameters(SqliteCommand command, PackageMetadata package)
    {
        command.Parameters.AddWithValue("$id", package.Id);
        command.Parameters.AddWithValue("$normalizedVersion", package.NormalizedVersion);
        command.Parameters.AddWithValue("$originalVersion", package.OriginalVersion);
        command.Parameters.AddWithValue("$description", package.Description);
        command.Parameters.AddWithValue("$authors", package.Authors);
        command.Parameters.AddWithValue("$tags", package.Tags);
        command.Parameters.AddWithValue("$nuspec", package.Nuspec);
        command.Parameters.AddWithValue(
            "$published",
            package.Published.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$listed", package.IsListed ? 1 : 0);
        command.Parameters.AddWithValue("$length", package.ContentLength);
        command.Parameters.AddWithValue("$blobPath", package.BlobPath);
        command.Parameters.AddWithValue("$sha256", package.Sha256);
    }
}

internal static class SqliteRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            SQLitePCL.raw.FreezeProvider();
            _initialized = true;
        }
    }
}

public sealed class PackageStorageInUseException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class PackageStorageCorruptionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
