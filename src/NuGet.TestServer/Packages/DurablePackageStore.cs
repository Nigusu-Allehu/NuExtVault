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
            RecoverPendingDeletes();
            ImportUntrackedBlobs();
            ValidateTrackedBlobs();
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
            if (_metadata.Find(package.Identity.Id, package.NormalizedVersion) is not null)
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

            package.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var metadata = _metadata.Find(id, Normalize(version));
        return ValueTask.FromResult(metadata is null ? null : Project(metadata));
    }

    public ValueTask<IReadOnlyList<TestPackage>> FindByIdAsync(
        string id,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Project(_metadata.FindById(id)));
    }

    public ValueTask<IReadOnlyList<TestPackage>> GetAllAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Project(_metadata.GetAll()));
    }

    public ValueTask<PackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var page = _metadata.Search(query ?? string.Empty, includePrerelease, skip, take);
        IReadOnlyList<PackageSearchItem> items = page.Items
            .Select(item => new PackageSearchItem(
                Project(item.Package),
                Project(item.Versions)))
            .ToArray();
        return ValueTask.FromResult(new PackageSearchPage(page.TotalHits, items));
    }

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

    private void ValidateTrackedBlobs()
    {
        foreach (var blob in _metadata.GetBlobReferences())
        {
            var fullPath = _blobs.GetFullPath(blob.BlobPath);
            if (!File.Exists(fullPath))
            {
                throw Corruption(blob.Id, blob.NormalizedVersion, "blob is missing");
            }

            if (new FileInfo(fullPath).Length != blob.ContentLength)
            {
                throw Corruption(blob.Id, blob.NormalizedVersion, "blob length does not match durable metadata");
            }
        }
    }

    private static PackageStorageCorruptionException Corruption(
        string id,
        string normalizedVersion,
        string detail,
        Exception? innerException = null) =>
        new(
            $"Package storage is corrupt for '{id} {normalizedVersion}': {detail}.",
            innerException);

    private TestPackage Project(PackageMetadata metadata) =>
        TestPackage.FromMetadata(metadata, _blobs.GetFullPath(metadata.BlobPath));

    private IReadOnlyList<TestPackage> Project(IReadOnlyList<PackageMetadata> packages) =>
        packages.Select(Project).ToArray();

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
    IReadOnlyList<PackageMetadata> FindById(string id);
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

internal sealed record PackageBlobReference(
    string Id,
    string NormalizedVersion,
    long ContentLength,
    string BlobPath);

internal sealed record PackageMetadataSearchPage(
    int TotalHits,
    IReadOnlyList<PackageMetadataSearchItem> Items);

internal sealed record PackageMetadataSearchItem(
    PackageMetadata Package,
    IReadOnlyList<PackageMetadata> Versions);

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
    private const int CurrentSchemaVersion = 2;
    private const string Columns =
        """
        id, normalized_version, original_version, description, authors, tags,
        nuspec, published_utc, is_listed, content_length, blob_path, sha256
        """;
    private const string QualifiedColumns =
        """
        package.id, package.normalized_version, package.original_version,
        package.description, package.authors, package.tags, package.nuspec,
        package.published_utc, package.is_listed, package.content_length,
        package.blob_path, package.sha256
        """;
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
            $"SELECT {Columns} FROM packages ORDER BY normalized_id, version_sort_key;";
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
            $"SELECT {Columns} FROM packages " +
            "WHERE normalized_id = $id AND normalized_version = $version;";
        command.Parameters.AddWithValue("$id", NormalizeId(id));
        command.Parameters.AddWithValue("$version", normalizedVersion);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<PackageMetadata> FindById(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM packages " +
            "WHERE normalized_id = $id ORDER BY version_sort_key;";
        command.Parameters.AddWithValue("$id", NormalizeId(id));
        using var reader = command.ExecuteReader();
        var packages = new List<PackageMetadata>();
        while (reader.Read())
        {
            packages.Add(Read(reader));
        }

        return packages.OrderBy(package => NuGetVersion.Parse(package.OriginalVersion)).ToArray();
    }

    public IReadOnlyList<PackageBlobReference> GetBlobReferences()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, normalized_version, content_length, blob_path
            FROM packages;
            """;
        using var reader = command.ExecuteReader();
        var blobs = new List<PackageBlobReference>();
        while (reader.Read())
        {
            blobs.Add(new PackageBlobReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3)));
        }

        return blobs;
    }

    public PackageMetadataSearchPage Search(
        string query,
        bool includePrerelease,
        int skip,
        int take)
    {
        take = Math.Clamp(take, 0, 1000);
        skip = Math.Max(skip, 0);
        var pattern = $"%{EscapeLike(query.ToLowerInvariant())}%";
        var match = QuoteFtsPhrase(query.ToLowerInvariant());
        var matchingIds = query.Length switch
        {
            0 =>
                """
                SELECT package.normalized_id
                FROM packages AS package
                WHERE package.is_listed = 1
                  AND ($prerelease = 1 OR package.is_prerelease = 0)
                GROUP BY package.normalized_id
                """,
            >= 3 =>
                """
                SELECT package.normalized_id
                FROM packages_search
                JOIN packages AS package ON package.rowid = packages_search.rowid
                WHERE package.is_listed = 1
                  AND ($prerelease = 1 OR package.is_prerelease = 0)
                  AND packages_search MATCH $match
                  AND package.search_text LIKE $pattern ESCAPE '\'
                GROUP BY package.normalized_id
                """,
            _ =>
                """
                SELECT package.normalized_id
                FROM packages AS package
                WHERE package.is_listed = 1
                  AND ($prerelease = 1 OR package.is_prerelease = 0)
                  AND package.search_text LIKE $pattern ESCAPE '\'
                GROUP BY package.normalized_id
                """
        };
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var countCommand = CreateSearchCommand(
            connection,
            transaction,
            $"""
            SELECT count(*)
            FROM (
                {matchingIds}
            );
            """,
            pattern,
            match,
            includePrerelease,
            skip,
            take);
        var totalHits = Convert.ToInt32(countCommand.ExecuteScalar());

        using var pageCommand = CreateSearchCommand(
            connection,
            transaction,
            $"""
            WITH matching_ids AS (
                {matchingIds}
                ORDER BY package.normalized_id
                LIMIT $take OFFSET $skip
            )
            SELECT {QualifiedColumns}
            FROM packages AS package
            JOIN matching_ids ON matching_ids.normalized_id = package.normalized_id
            WHERE package.is_listed = 1
              AND ($prerelease = 1 OR package.is_prerelease = 0)
            ORDER BY package.normalized_id, package.version_sort_key;
            """,
            pattern,
            match,
            includePrerelease,
            skip,
            take);
        using var reader = pageCommand.ExecuteReader();
        var pagePackages = new List<PackageMetadata>();
        while (reader.Read())
        {
            pagePackages.Add(Read(reader));
        }

        IReadOnlyList<PackageMetadataSearchItem> items = pagePackages
            .GroupBy(package => NormalizeId(package.Id))
            .Select(group =>
            {
                IReadOnlyList<PackageMetadata> versions = group
                    .OrderBy(package => NuGetVersion.Parse(package.OriginalVersion))
                    .ToArray();
                return new PackageMetadataSearchItem(versions[^1], versions);
            })
            .ToArray();
        transaction.Commit();
        return new PackageMetadataSearchPage(totalHits, items);
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
                id, normalized_id, normalized_version, original_version, is_prerelease,
                version_sort_key, description, authors, tags, search_text, nuspec,
                published_utc, is_listed, content_length, blob_path, sha256)
            VALUES (
                $id, $normalizedId, $normalizedVersion, $originalVersion, $isPrerelease,
                $versionSortKey, $description, $authors, $tags, $searchText, $nuspec,
                $published, $listed, $length, $blobPath, $sha256);
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
            WHERE normalized_id = $id AND normalized_version = $version;
            """;
        command.Parameters.AddWithValue("$listed", listed ? 1 : 0);
        command.Parameters.AddWithValue("$id", NormalizeId(id));
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
            "DELETE FROM packages WHERE normalized_id = $id AND normalized_version = $version;";
        command.Parameters.AddWithValue("$id", NormalizeId(id));
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
            CreateSchema(connection);
            return;
        }

        if (version == 1)
        {
            MigrateVersionOne(connection);
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE packages (
                id TEXT NOT NULL COLLATE NOCASE,
                normalized_id TEXT NOT NULL,
                normalized_version TEXT NOT NULL,
                original_version TEXT NOT NULL,
                is_prerelease INTEGER NOT NULL CHECK (is_prerelease IN (0, 1)),
                version_sort_key TEXT NOT NULL,
                description TEXT NOT NULL,
                authors TEXT NOT NULL,
                tags TEXT NOT NULL,
                search_text TEXT NOT NULL,
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
            """ +
            CreateIndexedSchemaSql +
            """
            INSERT INTO storage_migrations(version, applied_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            PRAGMA user_version = 2;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void MigrateVersionOne(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var alter = connection.CreateCommand())
        {
            alter.Transaction = transaction;
            alter.CommandText =
                """
                ALTER TABLE packages ADD COLUMN normalized_id TEXT NOT NULL DEFAULT '';
                ALTER TABLE packages ADD COLUMN is_prerelease INTEGER NOT NULL DEFAULT 0
                    CHECK (is_prerelease IN (0, 1));
                ALTER TABLE packages ADD COLUMN version_sort_key TEXT NOT NULL DEFAULT '';
                ALTER TABLE packages ADD COLUMN search_text TEXT NOT NULL DEFAULT '';
                """;
            alter.ExecuteNonQuery();
        }

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT rowid, id, original_version, description, tags FROM packages;";
            using var reader = select.ExecuteReader();
            var updates = new List<(long RowId, string Id, string Version, string Description, string Tags)>();
            while (reader.Read())
            {
                updates.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            reader.Close();
            foreach (var update in updates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE packages
                    SET normalized_id = $normalizedId,
                        is_prerelease = $isPrerelease,
                        version_sort_key = $versionSortKey,
                        search_text = $searchText
                    WHERE rowid = $rowId;
                    """;
                var version = NuGetVersion.Parse(update.Version);
                command.Parameters.AddWithValue("$normalizedId", NormalizeId(update.Id));
                command.Parameters.AddWithValue("$isPrerelease", version.IsPrerelease ? 1 : 0);
                command.Parameters.AddWithValue("$versionSortKey", VersionSortKey(version));
                command.Parameters.AddWithValue(
                    "$searchText",
                    SearchText(update.Id, update.Description, update.Tags));
                command.Parameters.AddWithValue("$rowId", update.RowId);
                command.ExecuteNonQuery();
            }
        }

        using var finalize = connection.CreateCommand();
        finalize.Transaction = transaction;
        finalize.CommandText =
            CreateIndexedSchemaSql +
            """
            INSERT INTO packages_search(packages_search) VALUES('rebuild');
            INSERT INTO storage_migrations(version, applied_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            PRAGMA user_version = 2;
            """;
        finalize.ExecuteNonQuery();
        transaction.Commit();
    }

    private const string CreateIndexedSchemaSql =
        """
        CREATE UNIQUE INDEX ix_packages_identity
            ON packages(normalized_id, normalized_version);
        CREATE INDEX ix_packages_registration
            ON packages(normalized_id, version_sort_key);
        CREATE INDEX ix_packages_search_page
            ON packages(is_listed, is_prerelease, normalized_id);
        CREATE VIRTUAL TABLE packages_search USING fts5(
            search_text,
            content='packages',
            content_rowid='rowid',
            tokenize='trigram'
        );
        CREATE TRIGGER packages_search_insert AFTER INSERT ON packages BEGIN
            INSERT INTO packages_search(rowid, search_text)
            VALUES (new.rowid, new.search_text);
        END;
        CREATE TRIGGER packages_search_delete AFTER DELETE ON packages BEGIN
            INSERT INTO packages_search(packages_search, rowid, search_text)
            VALUES ('delete', old.rowid, old.search_text);
        END;
        CREATE TRIGGER packages_search_update AFTER UPDATE ON packages BEGIN
            INSERT INTO packages_search(packages_search, rowid, search_text)
            VALUES ('delete', old.rowid, old.search_text);
            INSERT INTO packages_search(rowid, search_text)
            VALUES (new.rowid, new.search_text);
        END;
        """;

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
        var version = NuGetVersion.Parse(package.OriginalVersion);
        command.Parameters.AddWithValue("$id", package.Id);
        command.Parameters.AddWithValue("$normalizedId", NormalizeId(package.Id));
        command.Parameters.AddWithValue("$normalizedVersion", package.NormalizedVersion);
        command.Parameters.AddWithValue("$originalVersion", package.OriginalVersion);
        command.Parameters.AddWithValue("$isPrerelease", version.IsPrerelease ? 1 : 0);
        command.Parameters.AddWithValue("$versionSortKey", VersionSortKey(version));
        command.Parameters.AddWithValue("$description", package.Description);
        command.Parameters.AddWithValue("$authors", package.Authors);
        command.Parameters.AddWithValue("$tags", package.Tags);
        command.Parameters.AddWithValue(
            "$searchText",
            SearchText(package.Id, package.Description, package.Tags));
        command.Parameters.AddWithValue("$nuspec", package.Nuspec);
        command.Parameters.AddWithValue(
            "$published",
            package.Published.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$listed", package.IsListed ? 1 : 0);
        command.Parameters.AddWithValue("$length", package.ContentLength);
        command.Parameters.AddWithValue("$blobPath", package.BlobPath);
        command.Parameters.AddWithValue("$sha256", package.Sha256);
    }

    private static SqliteCommand CreateSearchCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string text,
        string pattern,
        string match,
        bool includePrerelease,
        int skip,
        int take)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        command.Parameters.AddWithValue("$pattern", pattern);
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$prerelease", includePrerelease ? 1 : 0);
        command.Parameters.AddWithValue("$skip", skip);
        command.Parameters.AddWithValue("$take", take);
        return command;
    }

    private static string NormalizeId(string id) => id.ToLowerInvariant();

    private static string SearchText(string id, string description, string tags) =>
        $"{id}\n{description}\n{tags}".ToLowerInvariant();

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string QuoteFtsPhrase(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string VersionSortKey(NuGetVersion version)
    {
        var release = version.IsPrerelease
            ? "-" + string.Join(
                ".",
                version.ReleaseLabels.Select(label =>
                    long.TryParse(label, out var number)
                        ? $"0{number.ToString().Length:D5}{number}"
                        : $"1{label.ToLowerInvariant()}"))
            : "~";
        return $"{version.Major:D10}.{version.Minor:D10}.{version.Patch:D10}.{version.Revision:D10}{release}";
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
