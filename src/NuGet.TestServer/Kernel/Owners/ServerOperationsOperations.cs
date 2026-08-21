using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Operations;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Health, diagnostics, backup, and restore owners.
/// </summary>
internal sealed class ServerOperationsOperations(IServerOperationsCapability operations)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.Operations,
            new DelegateOperationOwner<GetLivenessRequest, GetLivenessResponse>(
                OperationIds.HealthGetLiveness,
                GetLivenessAsync));
        builder.Register(
            BuiltInExtensionIds.Operations,
            new DelegateOperationOwner<GetReadinessRequest, GetReadinessResponse>(
                OperationIds.HealthGetReadiness,
                GetReadinessAsync));
        builder.Register(
            BuiltInExtensionIds.Operations,
            new DelegateOperationOwner<GetStorageHealthRequest, GetStorageHealthResponse>(
                OperationIds.HealthGetStorage,
                GetStorageHealthAsync));
        builder.Register(
            BuiltInExtensionIds.Operations,
            new DelegateOperationOwner<GetDiagnosticsRequest, GetDiagnosticsResponse>(
                OperationIds.DiagnosticsGet,
                GetDiagnosticsAsync));
        builder.Register(
            BuiltInExtensionIds.Operations,
            new DelegateOperationOwner<CreateBackupRequest, CreateBackupResponse>(
                OperationIds.BackupCreate,
                CreateBackupAsync));
        builder.Register(
            BuiltInExtensionIds.Operations,
            new DelegateOperationOwner<RestoreBackupRequest, RestoreBackupResponse>(
                OperationIds.RestoreExecute,
                RestoreBackupAsync));
    }

    private ValueTask<OperationResponse<GetLivenessResponse>> GetLivenessAsync(
        GetLivenessRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = new GetLivenessResponse(
            "healthy",
            operations.Mode);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(new { status = response.Status, mode = response.Mode })));
        return ValueTask.FromResult(OperationResponse<GetLivenessResponse>.Success(response));
    }

    private async ValueTask<OperationResponse<GetReadinessResponse>> GetReadinessAsync(
        GetReadinessRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var report = await operations.GetReadinessAsync(token);
        var response = new GetReadinessResponse(report.Status, report.Dependency, report.Ready);
        context.Complete(new OperationResult(
            response.Ready
                ? OperationResultStatus.Ok
                : OperationResultStatus.Unavailable,
            new OperationDocumentBody(new
            {
                status = response.Status,
                dependency = response.Dependency
            })));
        return OperationResponse<GetReadinessResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetStorageHealthResponse>> GetStorageHealthAsync(
        GetStorageHealthRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var report = await operations.GetStorageReportAsync(token);

        // The typed contract carries the dependency status only. Storage roots stay
        // inside the kernel, so the current report is rendered without being copied
        // into the contract.
        var response = new GetStorageHealthResponse(
            report.Status,
            [
                new StorageHealthItemDocument("dependency", report.Dependency, report.Reason),
                new StorageHealthItemDocument(
                    "packages",
                    report.PackageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    null),
                new StorageHealthItemDocument(
                    "storage-bytes",
                    report.StorageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    null),
                new StorageHealthItemDocument(
                    "vulnerability-snapshots",
                    report.VulnerabilitySnapshotCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    report.VulnerabilitySnapshotRetentionLimit.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
            ]);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(report)));
        return OperationResponse<GetStorageHealthResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetDiagnosticsResponse>> GetDiagnosticsAsync(
        GetDiagnosticsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var diagnostics = await operations.GetDiagnosticsAsync(token);
        var response = new GetDiagnosticsResponse(
            diagnostics.RequestCount,
            diagnostics.FailedRequestCount,
            diagnostics.PublishedPackageCount,
            diagnostics.StorageFailureCount);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(response)));
        return OperationResponse<GetDiagnosticsResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<CreateBackupResponse>> CreateBackupAsync(
        CreateBackupRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var manifest = await operations.CreateBackupAsync(context, request.Destination, token);
        if (manifest is null)
        {
            return OperationResponse<CreateBackupResponse>.Failure(
                OperationErrorPolicy.Unavailable(
                    "Backups require durable storage."));
        }

        return OperationResponse<CreateBackupResponse>.Success(
            new CreateBackupResponse(CreateManifest(manifest)));
    }

    private async ValueTask<OperationResponse<RestoreBackupResponse>> RestoreBackupAsync(
        RestoreBackupRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var manifest = await operations.RestoreAsync(context, request.Source, token);
        if (manifest is null)
        {
            return OperationResponse<RestoreBackupResponse>.Failure(
                OperationErrorPolicy.Unavailable(
                    "Restores require durable storage."));
        }

        return OperationResponse<RestoreBackupResponse>.Success(
            new RestoreBackupResponse(CreateManifest(manifest)));
    }

    private static BackupManifestDocument CreateManifest(StorageBackupManifest manifest) =>
        new(
            manifest.Version,
            manifest.CreatedAt,
            [
                .. manifest.Files.Select(file => new BackupEntryDocument(
                    file.Path,
                    file.Length,
                    file.Sha256))
            ]);
}
