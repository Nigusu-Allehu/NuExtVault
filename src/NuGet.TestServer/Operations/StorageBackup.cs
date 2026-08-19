using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NuGet.TestServer.Operations;

public static class StorageBackup
{
    private const string ManifestEntryName = "manifest.json";
    private const long MaximumManifestBytes = 10 * 1024 * 1024;
    private const long RestoreFreeSpaceReserveBytes = 256 * 1024 * 1024;
    private static readonly string[] IncludedDirectories =
        ["packages", "security", "trash", "vulnerabilities"];
    private static readonly string[] IncludedFiles =
    [
        "packages.db",
        "packages.db-shm",
        "packages.db-wal",
        "supply-chain.db",
        "supply-chain.db-shm",
        "supply-chain.db-wal"
    ];

    public static async Task<StorageBackupManifest> CreateAsync(
        string storageDirectory,
        string backupPath,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var root = Path.GetFullPath(storageDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Storage directory '{root}' does not exist.");
        }

        var destination = Path.GetFullPath(backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            throw new IOException($"Backup '{destination}' already exists.");
        }

        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        using var storageLease = AcquireStorageLease(root);
        try
        {
            var files = new List<StorageBackupFile>();
            await using (var stream = File.Create(temporary))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var directoryName in IncludedDirectories)
                {
                    var directory = Path.Combine(root, directoryName);
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(
                                 directory,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                        await using var input = File.OpenRead(file);
                        await using var output = entry.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        long length = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, token)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                            hash.AppendData(buffer, 0, read);
                            length += read;
                        }

                        files.Add(new StorageBackupFile(
                            relativePath,
                            length,
                            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                    }
                }

                foreach (var fileName in IncludedFiles)
                {
                    var file = Path.Combine(root, fileName);
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    await AddFileAsync(archive, root, file, files, token);
                }

                var manifest = new StorageBackupManifest(
                    Version: 1,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Files: files);
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: token);
            }

            File.Move(temporary, destination);
            return await ReadManifestAsync(destination, token);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    public static async Task<StorageBackupManifest> RestoreAsync(
        string backupPath,
        string storageDirectory,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var source = Path.GetFullPath(backupPath);
        var destination = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(destination);
        using var storageLease = AcquireStorageLease(destination);
        EnsureRestoreTargetIsClean(destination);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Storage directory must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".nuget-test-server-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            using var archive = ZipFile.OpenRead(source);
            var entries = archive.Entries.ToDictionary(
                entry => entry.FullName,
                StringComparer.Ordinal);
            if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry))
            {
                throw new InvalidDataException("Backup has no integrity manifest.");
            }

            if (manifestEntry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Backup integrity manifest is too large.");
            }

            StorageBackupManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<StorageBackupManifest>(
                    stream,
                    cancellationToken: token)
                    ?? throw new InvalidDataException("Backup integrity manifest is empty.");
            }

            if (manifest.Version != 1)
            {
                throw new InvalidDataException(
                    $"Backup manifest version '{manifest.Version}' is not supported.");
            }

            if (manifest.Files is null)
            {
                throw new InvalidDataException("Backup integrity manifest has no file list.");
            }

            long requiredBytes = 0;
            try
            {
                foreach (var file in manifest.Files)
                {
                    requiredBytes = checked(requiredBytes + file.Length);
                }
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Backup integrity manifest contains an invalid total length.",
                    exception);
            }

            var rootPath = Path.GetPathRoot(parent);
            if (!string.IsNullOrEmpty(rootPath))
            {
                var availableBytes = new DriveInfo(rootPath).AvailableFreeSpace;
                if (requiredBytes > Math.Max(0, availableBytes - RestoreFreeSpaceReserveBytes))
                {
                    throw new IOException(
                        "The restore volume does not have enough free space for the backup.");
                }
            }

            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                ValidateRelativePath(file.Path);
                if (!entries.TryGetValue(file.Path, out var entry))
                {
                    throw new InvalidDataException(
                        $"Backup file '{file.Path}' is missing from the archive.");
                }

                if (file.Length < 0 || entry.Length != file.Length)
                {
                    throw new InvalidDataException(
                        $"Backup file '{file.Path}' failed integrity validation.");
                }

                var target = Path.GetFullPath(
                    Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(
                        staging + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Backup path '{file.Path}' is unsafe.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = File.Create(target);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long length = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    if (length + read > file.Length)
                    {
                        throw new InvalidDataException(
                            $"Backup file '{file.Path}' failed integrity validation.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }

                var actualHash = hash.GetHashAndReset();
                byte[] expectedHash;
                try
                {
                    expectedHash = Convert.FromHexString(file.Sha256);
                }
                catch (Exception exception) when (
                    exception is FormatException or ArgumentNullException)
                {
                    throw new InvalidDataException(
                        $"Backup integrity hash for '{file.Path}' is invalid.",
                        exception);
                }

                if (length != file.Length ||
                    !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                {
                    throw new InvalidDataException(
                        $"Backup file '{file.Path}' failed integrity validation.");
                }
            }

            Directory.CreateDirectory(destination);
            foreach (var directoryName in IncludedDirectories)
            {
                var stagedDirectory = Path.Combine(staging, directoryName);
                if (Directory.Exists(stagedDirectory))
                {
                    Directory.Move(stagedDirectory, Path.Combine(destination, directoryName));
                }
            }

            foreach (var fileName in IncludedFiles)
            {
                var stagedFile = Path.Combine(staging, fileName);
                if (File.Exists(stagedFile))
                {
                    File.Move(stagedFile, Path.Combine(destination, fileName));
                }
            }

            return manifest;
        }
        catch
        {
            foreach (var directoryName in IncludedDirectories)
            {
                var restoredDirectory = Path.Combine(destination, directoryName);
                if (Directory.Exists(restoredDirectory))
                {
                    Directory.Delete(restoredDirectory, recursive: true);
                }
            }

            foreach (var fileName in IncludedFiles)
            {
                var restoredFile = Path.Combine(destination, fileName);
                if (File.Exists(restoredFile))
                {
                    File.Delete(restoredFile);
                }
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<StorageBackupManifest> ReadManifestAsync(
        string backupPath,
        CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Backup has no integrity manifest.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<StorageBackupManifest>(
            stream,
            cancellationToken: token)
            ?? throw new InvalidDataException("Backup integrity manifest is empty.");
    }

    private static void EnsureRestoreTargetIsClean(string destination)
    {
        foreach (var directoryName in IncludedDirectories)
        {
            if (Directory.Exists(Path.Combine(destination, directoryName)))
            {
                throw new IOException(
                    $"Restore target '{destination}' already contains '{directoryName}'.");
            }
        }

        foreach (var fileName in IncludedFiles)
        {
            if (File.Exists(Path.Combine(destination, fileName)))
            {
                throw new IOException(
                    $"Restore target '{destination}' already contains '{fileName}'.");
            }
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathFullyQualified(path) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            !IncludedDirectories.Any(
                directory => path.StartsWith(directory + "/", StringComparison.Ordinal)) &&
            !IncludedFiles.Contains(path, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Backup path '{path}' is invalid.");
        }
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string root,
        string file,
        List<StorageBackupFile> files,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
        await using var input = File.OpenRead(file);
        await using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        files.Add(new StorageBackupFile(
            relativePath,
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
    }

    private static FileStream AcquireStorageLease(string root)
    {
        var lockPath = Path.Combine(root, ".storage.lock");
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new IOException(
                $"Storage directory '{root}' is in use. Stop the server before creating a backup.",
                exception);
        }
    }
}

public sealed record StorageBackupManifest(
    int Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StorageBackupFile> Files);

public sealed record StorageBackupFile(string Path, long Length, string Sha256);
