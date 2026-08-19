using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed class PackageSupplyChainService : IAsyncDisposable
{
    private readonly IPackageStore _inner;
    private readonly SupplyChainOptions _options;
    private readonly IPackagePolicyScanner _scanner;
    private readonly TimeProvider _timeProvider;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PackageSupplyChainService(
        IPackageStore inner,
        string? storageDirectory = null,
        SupplyChainOptions? options = null,
        IPackagePolicyScanner? scanner = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = (options ?? new SupplyChainOptions()).Validate();
        _scanner = scanner ?? new SafePackagePolicyScanner();
        _timeProvider = timeProvider ?? TimeProvider.System;
        SqliteRuntime.Initialize();
        var dataSource = storageDirectory is null
            ? ":memory:"
            : Path.Combine(Path.GetFullPath(storageDirectory), "supply-chain.db");
        var trustUntrackedPackages = storageDirectory is null;
        if (storageDirectory is not null)
        {
            Directory.CreateDirectory(storageDirectory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            _connection.Open();
            Migrate();
            ImportUntrackedPackages(trustUntrackedPackages);
            SynchronizeModerationStates();
            RecoverDeletedPackages();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public async ValueTask<PackagePublicationResult> PublishAsync(
        PackagePublicationRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Package);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Repository);
        await _gate.WaitAsync(token);
        try
        {
            var package = request.Package;
            var id = package.Identity.Id;
            var version = package.NormalizedVersion;
            var hash = await ComputeHashAsync(package, token);
            var existing = ReadStatus(id, version);
            if (existing is not null)
            {
                package.Dispose();
                if (CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(existing.ContentHash),
                        hash))
                {
                    var outcome = existing.State switch
                    {
                        PackageModerationState.Published => PackagePublicationOutcome.Duplicate,
                        PackageModerationState.Rejected => PackagePublicationOutcome.Rejected,
                        PackageModerationState.Quarantined => PackagePublicationOutcome.Quarantined,
                        _ => PackagePublicationOutcome.Conflict
                    };
                    Audit(
                        id,
                        version,
                        request.Identity,
                        "publish",
                        outcome.ToString().ToLowerInvariant(),
                        $"Identical content is already {existing.State.ToString().ToLowerInvariant()}.");
                    return new(
                        outcome,
                        $"Identical package content is already {existing.State.ToString().ToLowerInvariant()}.");
                }

                Audit(id, version, request.Identity, "publish", "conflict", "Published versions are immutable.");
                return new(PackagePublicationOutcome.Conflict, "The package version already has different content.");
            }

            var owner = GetOwner(id);
            if (!request.Administrator && owner is null && HasExistingPackageId(id))
            {
                package.Dispose();
                Audit(
                    id,
                    version,
                    request.Identity,
                    "publish",
                    "unauthorized",
                    "Existing unowned package IDs require administrator assignment.");
                return new(
                    PackagePublicationOutcome.Unauthorized,
                    "The existing package ID has no assignable owner.");
            }

            if (!request.Administrator &&
                owner is not null &&
                !string.Equals(owner, request.Identity, StringComparison.Ordinal))
            {
                package.Dispose();
                Audit(id, version, request.Identity, "publish", "unauthorized", "Package ID is owned by another identity.");
                return new(PackagePublicationOutcome.Unauthorized, "The package ID is owned by another identity.");
            }

            if (!request.Administrator && !AllowsReservedNamespace(id, request.Identity))
            {
                package.Dispose();
                Audit(id, version, request.Identity, "publish", "unauthorized", "Package namespace is reserved.");
                return new(PackagePublicationOutcome.Unauthorized, "The package namespace is reserved.");
            }

            if (WouldExceedQuota(request.Identity, request.Repository, package.ContentLength))
            {
                package.Dispose();
                Audit(id, version, request.Identity, "publish", "quota-exceeded", "Publication quota exceeded.");
                return new(PackagePublicationOutcome.QuotaExceeded, "Publication quota exceeded.");
            }

            package = package with { ModerationState = PackageModerationState.Quarantined };
            await _inner.AddAsync(package, token);
            try
            {
                using var transaction = _connection.BeginTransaction();
                if (owner is null)
                {
                    Execute(
                        transaction,
                        "INSERT INTO package_owners (id, owner) VALUES ($id, $owner);",
                        ("$id", id),
                        ("$owner", request.Identity));
                }

                Execute(
                    transaction,
                    """
                    INSERT INTO package_supply_chain (
                        id, normalized_version, state, owner, repository, content_hash, content_length)
                    VALUES ($id, $version, $state, $owner, $repository, $hash, $length);
                    """,
                    ("$id", id),
                    ("$version", version),
                    ("$state", PackageModerationState.Quarantined.ToString()),
                    ("$owner", request.Identity),
                    ("$repository", request.Repository),
                    ("$hash", Convert.ToHexString(hash)),
                    ("$length", package.ContentLength));
                InsertAudit(
                    transaction,
                    id,
                    version,
                    request.Identity,
                    "publish",
                    "quarantined",
                    "Package is hidden while required validation runs.");
                transaction.Commit();
            }
            catch
            {
                await _inner.DeleteAsync(id, version, CancellationToken.None);
                throw;
            }

            var stored = await _inner.FindStoredAsync(id, version, token)
                ?? throw new PackageStorageCorruptionException(
                    $"Quarantined package '{id} {version}' is absent from blob storage.");
            var validations = new List<PackageValidationRecord>();
            var signature = await VerifySignatureAsync(stored, token);
            validations.Add(signature);
            if (signature.Outcome == "invalid" ||
                _options.RequireSignedPackages && signature.Outcome == "unsigned")
            {
                SetValidationState(
                    id,
                    version,
                    request.Identity,
                    PackageModerationState.Rejected,
                    validations,
                    signature.Detail);
                return new(PackagePublicationOutcome.Rejected, signature.Detail);
            }

            PackageScanResult scan;
            try
            {
                scan = await _scanner.ScanAsync(stored, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                validations.Add(new(
                    "policy-scanner",
                    "error",
                    exception.Message,
                    _timeProvider.GetUtcNow()));
                SetValidationState(
                    id,
                    version,
                    request.Identity,
                    PackageModerationState.Quarantined,
                    validations,
                    "Scanner failed; package remains quarantined.");
                return new(
                    PackagePublicationOutcome.Quarantined,
                    "Scanner failed; package remains quarantined.");
            }

            validations.Add(new(
                "policy-scanner",
                scan.Outcome.ToString().ToLowerInvariant(),
                scan.Detail,
                _timeProvider.GetUtcNow()));
            if (scan.Outcome != PackageScanOutcome.Clean)
            {
                var state = scan.Outcome == PackageScanOutcome.Malicious
                    ? PackageModerationState.Rejected
                    : PackageModerationState.Quarantined;
                SetValidationState(id, version, request.Identity, state, validations, scan.Detail);
                return new(
                    state == PackageModerationState.Rejected
                        ? PackagePublicationOutcome.Rejected
                        : PackagePublicationOutcome.Quarantined,
                    scan.Detail);
            }

            SetValidationState(
                id,
                version,
                request.Identity,
                PackageModerationState.Published,
                validations,
                "All required validation completed.");
            return new(PackagePublicationOutcome.Published, "Package published.");
        }
        catch
        {
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask AddAsync(TestPackage package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await _gate.WaitAsync(token);
        try
        {
            var hash = await ComputeHashAsync(package, token);
            await _inner.AddAsync(package, token);
            try
            {
                using var transaction = _connection.BeginTransaction();
                Execute(
                    transaction,
                    """
                    INSERT INTO package_supply_chain (
                        id, normalized_version, state, owner, repository, content_hash, content_length)
                    VALUES ($id, $version, $state, NULL, $repository, $hash, $length);
                    """,
                    ("$id", package.Identity.Id),
                    ("$version", package.NormalizedVersion),
                    ("$state", PackageModerationState.Published.ToString()),
                    ("$repository", "trusted-seed"),
                    ("$hash", Convert.ToHexString(hash)),
                    ("$length", package.ContentLength));
                InsertAudit(
                    transaction,
                    package.Identity.Id,
                    package.NormalizedVersion,
                    "system",
                    "seed",
                    "published",
                    "Trusted test-control seed bypassed publication validation.");
                transaction.Commit();
            }
            catch
            {
                await _inner.DeleteAsync(
                    package.Identity.Id,
                    package.NormalizedVersion,
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DeleteControlledAsync(
        string id,
        string version,
        string actor,
        string reason,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _gate.WaitAsync(token);
        try
        {
            var normalized = Normalize(version);
            if (ReadStatus(id, normalized) is null)
            {
                return false;
            }

            using var transaction = _connection.BeginTransaction();
            Execute(
                transaction,
                """
                UPDATE package_supply_chain
                SET state = $state
                WHERE id = $id COLLATE NOCASE AND normalized_version = $version;
                """,
                ("$state", PackageModerationState.Deleted.ToString()),
                ("$id", id),
                ("$version", normalized));
            InsertAudit(transaction, id, normalized, actor, "delete", "pending", reason);
            transaction.Commit();

            await _inner.DeleteAsync(id, normalized, token);
            Audit(id, normalized, actor, "delete", "deleted", reason);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> ModerateAsync(
        string id,
        string version,
        PackageModerationState state,
        string actor,
        string reason,
        CancellationToken token = default)
    {
        if (state == PackageModerationState.Deleted)
        {
            return await DeleteControlledAsync(id, version, actor, reason, token);
        }

        await _gate.WaitAsync(token);
        try
        {
            var normalized = Normalize(version);
            var status = ReadStatus(id, normalized);
            if (status is null || status.State == PackageModerationState.Deleted)
            {
                return false;
            }

            using var transaction = _connection.BeginTransaction();
            Execute(
                transaction,
                """
                UPDATE package_supply_chain
                SET state = $state
                WHERE id = $id COLLATE NOCASE AND normalized_version = $version;
                """,
                ("$state", state.ToString()),
                ("$id", id),
                ("$version", normalized));
            InsertAudit(
                transaction,
                id,
                normalized,
                actor,
                "moderate",
                state.ToString().ToLowerInvariant(),
                reason);
            transaction.Commit();
            if (!await _inner.SetModerationStateAsync(id, normalized, state, token))
            {
                throw new PackageStorageCorruptionException(
                    $"Supply-chain metadata exists for package '{id} {normalized}', but its package metadata is absent.");
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PackageSupplyChainStatus?> GetStatusAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadStatus(id, Normalize(version)));
    }

    public ValueTask<string?> GetOwnerAsync(
        string id,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetOwner(id));
    }

    public ValueTask<IReadOnlyList<PackageValidationRecord>> GetValidationResultsAsync(
        string id,
        string version,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT validator, outcome, detail, timestamp_utc
            FROM package_validations
            WHERE id = $id COLLATE NOCASE AND normalized_version = $version
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", Normalize(version));
        using var reader = command.ExecuteReader();
        var results = new List<PackageValidationRecord>();
        while (reader.Read())
        {
            results.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return ValueTask.FromResult<IReadOnlyList<PackageValidationRecord>>(results);
    }

    public ValueTask<IReadOnlyList<PackageSupplyChainAudit>> GetAuditHistoryAsync(
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, timestamp_utc, package_id, package_version,
                   actor, action, result, detail
            FROM package_supply_chain_audit
            ORDER BY sequence;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<PackageSupplyChainAudit>();
        while (reader.Read())
        {
            results.Add(new(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return ValueTask.FromResult<IReadOnlyList<PackageSupplyChainAudit>>(results);
    }

    public async ValueTask ResetAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await _inner.ResetAsync(token);
            using var transaction = _connection.BeginTransaction();
            Execute(transaction, "DELETE FROM package_validations;");
            Execute(transaction, "DELETE FROM package_supply_chain;");
            Execute(transaction, "DELETE FROM package_owners;");
            Execute(transaction, "DELETE FROM package_supply_chain_audit;");
            transaction.Commit();
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
            await _connection.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void Migrate()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS package_supply_chain (
                id TEXT NOT NULL COLLATE NOCASE,
                normalized_version TEXT NOT NULL,
                state TEXT NOT NULL,
                owner TEXT NULL,
                repository TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                content_length INTEGER NOT NULL,
                PRIMARY KEY (id, normalized_version));
            CREATE TABLE IF NOT EXISTS package_owners (
                id TEXT PRIMARY KEY COLLATE NOCASE,
                owner TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS package_validations (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL COLLATE NOCASE,
                normalized_version TEXT NOT NULL,
                validator TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS package_supply_chain_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                package_id TEXT NULL COLLATE NOCASE,
                package_version TEXT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                result TEXT NOT NULL,
                detail TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private void ImportUntrackedPackages(bool trustAsLegacy)
    {
        var packages = _inner.GetAllStoredAsync().AsTask().GetAwaiter().GetResult();
        foreach (var package in packages)
        {
            if (ReadStatus(package.Identity.Id, package.NormalizedVersion) is not null)
            {
                continue;
            }

            var hash = ComputeHashAsync(package, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            using var transaction = _connection.BeginTransaction();
            Execute(
                transaction,
                """
                INSERT INTO package_supply_chain (
                    id, normalized_version, state, owner, repository, content_hash, content_length)
                VALUES ($id, $version, $state, NULL, $repository, $hash, $length);
                """,
                ("$id", package.Identity.Id),
                ("$version", package.NormalizedVersion),
                ("$state", (trustAsLegacy
                    ? PackageModerationState.Published
                    : PackageModerationState.Quarantined).ToString()),
                ("$repository", trustAsLegacy ? "legacy" : "recovered"),
                ("$hash", Convert.ToHexString(hash)),
                ("$length", package.ContentLength));
            InsertAudit(
                transaction,
                package.Identity.Id,
                package.NormalizedVersion,
                "system",
                trustAsLegacy ? "import" : "recover",
                trustAsLegacy ? "published" : "quarantined",
                trustAsLegacy
                    ? "Existing durable package imported as published."
                    : "Untracked durable package recovered as quarantined after interrupted publication.");
            transaction.Commit();
            if (!_inner.SetModerationStateAsync(
                    package.Identity.Id,
                    package.NormalizedVersion,
                    trustAsLegacy
                        ? PackageModerationState.Published
                        : PackageModerationState.Quarantined)
                .AsTask().GetAwaiter().GetResult())
            {
                throw new PackageStorageCorruptionException(
                    $"Recovered supply-chain package '{package.Identity.Id} {package.NormalizedVersion}' is absent.");
            }
        }
    }

    private void RecoverDeletedPackages()
    {
        var deleted = new List<(string Id, string Version)>();
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, normalized_version
                FROM package_supply_chain
                WHERE state = $state;
                """;
            command.Parameters.AddWithValue("$state", PackageModerationState.Deleted.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                deleted.Add((reader.GetString(0), reader.GetString(1)));
            }

        }

        foreach (var package in deleted)
        {
            if (_inner.DeleteAsync(package.Id, package.Version)
                .AsTask().GetAwaiter().GetResult())
            {
                Audit(
                    package.Id,
                    package.Version,
                    "system",
                    "delete-recovery",
                    "deleted",
                    "Completed interrupted controlled deletion.");
            }
        }
    }

    private void SynchronizeModerationStates()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, normalized_version, state
            FROM package_supply_chain
            WHERE state <> $deleted;
            """;
        command.Parameters.AddWithValue("$deleted", PackageModerationState.Deleted.ToString());
        using var reader = command.ExecuteReader();
        var states = new List<(string Id, string Version, PackageModerationState State)>();
        while (reader.Read())
        {
            states.Add((
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<PackageModerationState>(reader.GetString(2), ignoreCase: true)));
        }

        foreach (var package in states)
        {
            if (!_inner.SetModerationStateAsync(package.Id, package.Version, package.State)
                .AsTask().GetAwaiter().GetResult())
            {
                throw new PackageStorageCorruptionException(
                    $"Supply-chain metadata exists for missing package '{package.Id} {package.Version}'.");
            }
        }
    }

    private async ValueTask<PackageValidationRecord> VerifySignatureAsync(
        TestPackage package,
        CancellationToken token)
    {
        try
        {
            await using var stream = package.OpenReadStream();
            using var reader = new PackageArchiveReader(stream, leaveStreamOpen: false);
            var signature = await reader.GetPrimarySignatureAsync(token);
            if (signature is null)
            {
                return new(
                    "nuget-signature",
                    "unsigned",
                    _options.RequireSignedPackages
                        ? "A package signature is required."
                        : "Package is unsigned and policy allows unsigned packages.",
                    _timeProvider.GetUtcNow());
            }

            var verifier = new PackageSignatureVerifier(
            [
                new IntegrityVerificationProvider()
            ]);
            var settings = SignedPackageVerifierSettings.GetAcceptModeDefaultPolicy();
            var result = await verifier.VerifySignaturesAsync(reader, settings, token);
            return new(
                "nuget-signature",
                result.IsValid ? "valid" : "invalid",
                result.IsValid
                    ? "NuGet package signature and signed content integrity are valid."
                    : "NuGet package signature or signed content integrity is invalid.",
                _timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                SignatureException or
                CryptographicException)
        {
            return new(
                "nuget-signature",
                "invalid",
                $"NuGet package signature is invalid: {exception.Message}",
                _timeProvider.GetUtcNow());
        }
    }

    private void SetValidationState(
        string id,
        string version,
        string actor,
        PackageModerationState state,
        IReadOnlyList<PackageValidationRecord> validations,
        string detail)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var validation in validations)
        {
            Execute(
                transaction,
                """
                INSERT INTO package_validations (
                    id, normalized_version, validator, outcome, detail, timestamp_utc)
                VALUES ($id, $version, $validator, $outcome, $detail, $timestamp);
                """,
                ("$id", id),
                ("$version", version),
                ("$validator", validation.Validator),
                ("$outcome", validation.Outcome),
                ("$detail", validation.Detail),
                ("$timestamp", validation.Timestamp.ToString("O")));
        }

        Execute(
            transaction,
            """
            UPDATE package_supply_chain
            SET state = $state
            WHERE id = $id COLLATE NOCASE AND normalized_version = $version;
            """,
            ("$state", state.ToString()),
            ("$id", id),
            ("$version", version));
        InsertAudit(
            transaction,
            id,
            version,
            actor,
            "validate",
            state.ToString().ToLowerInvariant(),
            detail);
        transaction.Commit();
        if (!_inner.SetModerationStateAsync(id, version, state)
            .AsTask().GetAwaiter().GetResult())
        {
            throw new PackageStorageCorruptionException(
                $"Supply-chain metadata exists for package '{id} {version}', but its package metadata is absent.");
        }
    }

    private bool AllowsReservedNamespace(string id, string identity) =>
        !_options.NamespaceReservations
            .Where(pair => id.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            .Any(pair => !string.Equals(pair.Value, identity, StringComparison.Ordinal));

    private bool WouldExceedQuota(string identity, string repository, long contentLength)
    {
        var identityUsage = ReadUsage("owner", identity);
        var repositoryUsage = ReadUsage("repository", repository);
        return identityUsage.Count >= _options.MaximumPackagesPerIdentity ||
               identityUsage.Bytes > _options.MaximumBytesPerIdentity - contentLength ||
               repositoryUsage.Count >= _options.MaximumPackagesPerRepository ||
               repositoryUsage.Bytes > _options.MaximumBytesPerRepository - contentLength;
    }

    private (long Count, long Bytes) ReadUsage(string column, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT COUNT(*), COALESCE(SUM(content_length), 0)
             FROM package_supply_chain
             WHERE {column} = $value AND state <> $deleted;
             """;
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$deleted", PackageModerationState.Deleted.ToString());
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private PackageSupplyChainStatus? ReadStatus(string id, string version)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, normalized_version, state, owner, repository, content_hash, content_length
            FROM package_supply_chain
            WHERE id = $id COLLATE NOCASE AND normalized_version = $version;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<PackageModerationState>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));
    }

    private string? GetOwner(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT owner FROM package_owners WHERE id = $id COLLATE NOCASE;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private bool HasExistingPackageId(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM package_supply_chain
                WHERE id = $id COLLATE NOCASE AND state <> $deleted);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$deleted", PackageModerationState.Deleted.ToString());
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private void Audit(
        string? id,
        string? version,
        string actor,
        string action,
        string result,
        string detail)
    {
        using var transaction = _connection.BeginTransaction();
        InsertAudit(transaction, id, version, actor, action, result, detail);
        transaction.Commit();
    }

    private void InsertAudit(
        SqliteTransaction transaction,
        string? id,
        string? version,
        string actor,
        string action,
        string result,
        string detail) =>
        Execute(
            transaction,
            """
            INSERT INTO package_supply_chain_audit (
                timestamp_utc, package_id, package_version, actor, action, result, detail)
            VALUES ($timestamp, $id, $version, $actor, $action, $result, $detail);
            """,
            ("$timestamp", _timeProvider.GetUtcNow().ToString("O")),
            ("$id", id),
            ("$version", version),
            ("$actor", actor),
            ("$action", action),
            ("$result", result),
            ("$detail", detail));

    private void Execute(
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static async ValueTask<byte[]> ComputeHashAsync(
        TestPackage package,
        CancellationToken token)
    {
        await using var stream = package.OpenReadStream();
        return await SHA256.HashDataAsync(stream, token);
    }

    private static string Normalize(string version) =>
        NuGetVersion.TryParse(version, out var parsed)
            ? TestPackage.NormalizeVersion(parsed)
            : version.ToLowerInvariant();
}
