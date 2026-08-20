using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NuGet.TestServer.Kernel.Capabilities;

internal sealed partial class ExtensionStateStore
{
    private readonly string? _root;
    private readonly ImmutableDictionary<
        string,
        ImmutableDictionary<string, LegacyStateFileSetRegistration>> _legacyFileSets;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.Ordinal);

    public ExtensionStateStore(
        string? root,
        ImmutableDictionary<
            string,
            ImmutableDictionary<string, LegacyStateFileSetRegistration>>? legacyFileSets = null)
    {
        _root = root is null ? null : Path.GetFullPath(root);
        _legacyFileSets = legacyFileSets ??
            ImmutableDictionary<
                string,
                ImmutableDictionary<string, LegacyStateFileSetRegistration>>.Empty;
    }

    public async ValueTask<ExtensionStateFileSet?> ReadLegacyFileSetAsync(
        string ownerId,
        string logicalName,
        CancellationToken token,
        long maximumBytes = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        token.ThrowIfCancellationRequested();
        if (!_legacyFileSets.TryGetValue(ownerId, out var ownerSets) ||
            !ownerSets.TryGetValue(logicalName, out var registration))
        {
            return null;
        }

        var root = Path.GetFullPath(registration.RootDirectory);
        if (!Directory.Exists(root))
        {
            return null;
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExtensionStateException(
                "Legacy extension state root cannot be a filesystem link.");
        }

        var maximumTotalBytes = Math.Min(maximumBytes, registration.MaximumTotalBytes);
        var maximumFileBytes = Math.Min(maximumBytes, registration.MaximumFileBytes);
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var files = ImmutableArray.CreateBuilder<ExtensionStateFile>();
        long totalBytes = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            foreach (var path in Directory.EnumerateFiles(root, "*", options)
                         .Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (files.Count >= registration.MaximumFileCount)
                {
                    throw new CapabilityStreamLimitExceededException(
                        files.Count + 1,
                        registration.MaximumFileCount);
                }

                var fullPath = Path.GetFullPath(path);
                var pathComparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!fullPath.StartsWith(rootPrefix, pathComparison) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ExtensionStateException(
                        "Legacy extension state contains an unsafe filesystem entry.");
                }

                var relative = Path.GetRelativePath(root, fullPath);
                if (!IsSafeRelativeName(relative))
                {
                    throw new ExtensionStateException(
                        "Legacy extension state contains an unsafe logical name.");
                }

                var declaredLength = new FileInfo(fullPath).Length;
                if (declaredLength > maximumFileBytes)
                {
                    throw new CapabilityStreamLimitExceededException(
                        declaredLength,
                        maximumFileBytes);
                }

                if (totalBytes + declaredLength > maximumTotalBytes)
                {
                    throw new CapabilityStreamLimitExceededException(
                        totalBytes + declaredLength,
                        maximumTotalBytes);
                }

                var content = await ReadBoundedAsync(
                    fullPath,
                    maximumFileBytes,
                    token);
                totalBytes += content.LongLength;
                if (totalBytes > maximumTotalBytes)
                {
                    throw new CapabilityStreamLimitExceededException(
                        totalBytes,
                        maximumTotalBytes);
                }

                files.Add(new ExtensionStateFile(
                    relative.Replace(Path.DirectorySeparatorChar, '/'),
                    content));
            }

            return new ExtensionStateFileSet(files.ToImmutable());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ExtensionStateException(
                "Legacy extension state could not be read.",
                exception);
        }
    }

    public async ValueTask<T?> ReadAsync<T>(
        string ownerId,
        string key,
        CancellationToken token,
        long maximumBytes = long.MaxValue)
    {
        var path = GetPath(ownerId, key);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            byte[] content;
            try
            {
                var length = new FileInfo(path).Length;
                if (length > maximumBytes)
                {
                    throw new CapabilityStreamLimitExceededException(length, maximumBytes);
                }

                content = await ReadBoundedAsync(path, maximumBytes, token);
                var envelope = JsonSerializer.Deserialize<StateEnvelope>(content)
                    ?? throw new ExtensionStateException("Extension state envelope is empty.");
                if (envelope.Version != 1 ||
                    !string.Equals(envelope.Key, key, StringComparison.Ordinal))
                {
                    throw new ExtensionStateException("Extension state metadata is invalid.");
                }

                var payload = Convert.FromBase64String(envelope.Payload);
                var expected = Convert.FromHexString(envelope.Sha256);
                var actual = SHA256.HashData(payload);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                {
                    throw new ExtensionStateException(
                        "Extension state integrity validation failed.");
                }

                return JsonSerializer.Deserialize<T>(payload);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (ExtensionStateException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                JsonException or FormatException or CryptographicException)
            {
                throw new ExtensionStateException(
                    "Extension state could not be read or validated.",
                    exception);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask WriteAsync<T>(
        string ownerId,
        string key,
        T value,
        CancellationToken token,
        long maximumBytes = long.MaxValue)
    {
        token.ThrowIfCancellationRequested();
        var path = GetPath(ownerId, key);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var payload = JsonSerializer.SerializeToUtf8Bytes(value);
            var envelope = new StateEnvelope(
                1,
                key,
                Convert.ToBase64String(payload),
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
            var content = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (content.LongLength > maximumBytes)
            {
                throw new CapabilityStreamLimitExceededException(
                    content.LongLength,
                    maximumBytes);
            }

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(content, token);
                    await stream.FlushAsync(token);
                    stream.Flush(flushToDisk: true);
                }

                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ExtensionStateException("Extension state could not be persisted.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(string ownerId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_root is null)
        {
            throw new ExtensionStateException("Durable extension state is not configured.");
        }

        if (!StateKeyRegex().IsMatch(key))
        {
            throw new ArgumentException(
                "Extension state keys may contain only letters, numbers, '.', '_' and '-'.",
                nameof(key));
        }

        var ownerNamespace = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)))
            .ToLowerInvariant();
        var keyName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();
        return Path.Combine(_root, ownerNamespace, $"{keyName}.json");
    }

    private static bool IsSafeRelativeName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !Path.IsPathRooted(name) &&
        name.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        long maximumBytes,
        CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new CapabilityStreamLimitExceededException(
                    destination.Length + read,
                    maximumBytes);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex StateKeyRegex();

    private sealed record StateEnvelope(int Version, string Key, string Payload, string Sha256);
}

internal sealed record LegacyStateFileSetRegistration(
    string RootDirectory,
    long MaximumFileBytes,
    long MaximumTotalBytes,
    int MaximumFileCount = 256)
{
    public LegacyStateFileSetRegistration Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootDirectory);
        if (MaximumFileBytes <= 0 ||
            MaximumTotalBytes <= 0 ||
            MaximumFileBytes > MaximumTotalBytes ||
            MaximumFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumTotalBytes),
                "Legacy state file-set limits are invalid.");
        }

        return this;
    }
}

internal sealed class ExtensionStateException : Exception
{
    public ExtensionStateException(string message)
        : base(message)
    {
    }

    public ExtensionStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
