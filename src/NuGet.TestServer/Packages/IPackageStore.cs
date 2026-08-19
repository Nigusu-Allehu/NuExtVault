namespace NuGet.TestServer.Packages;

public interface IPackageStore : IAsyncDisposable
{
    ValueTask AddAsync(TestPackage package, CancellationToken token = default);

    ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token = default);

    ValueTask<TestPackage?> FindStoredAsync(
        string id,
        string version,
        CancellationToken token = default);

    ValueTask<byte[]?> FindSymbolAsync(
        string id,
        string version,
        CancellationToken token = default);

    ValueTask AddSymbolAsync(byte[] content, CancellationToken token = default);

    ValueTask<IReadOnlyList<TestPackage>> FindByIdAsync(
        string id,
        CancellationToken token = default);

    ValueTask<IReadOnlyList<TestPackage>> GetAllAsync(CancellationToken token = default);

    ValueTask<IReadOnlyList<TestPackage>> GetAllStoredAsync(CancellationToken token = default);

    ValueTask<PackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        CancellationToken token = default,
        string? packageType = null);

    ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token = default);

    ValueTask<bool> SetRepositoryMetadataAsync(
        string id,
        string version,
        PackageRepositoryMetadata metadata,
        CancellationToken token = default);

    ValueTask<bool> SetModerationStateAsync(
        string id,
        string version,
        PackageModerationState state,
        CancellationToken token = default);

    ValueTask<bool> DeleteAsync(
        string id,
        string version,
        CancellationToken token = default);

    ValueTask ResetAsync(CancellationToken token = default);
}
