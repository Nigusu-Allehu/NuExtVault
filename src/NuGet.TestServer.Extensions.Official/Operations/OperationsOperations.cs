using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Operations;

internal sealed class OperationsOperations(
    IOperationsQueryCapability query,
    IBackupCheckpointCapability backup,
    IRestoreCheckpointCapability restore)
{
    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            OperationsModule.ExtensionId,
            OperationOwner.Create<GetLivenessRequest, GetLivenessResponse>(
                OperationIds.HealthGetLiveness,
                GetLivenessAsync));
        registry.Register(
            OperationsModule.ExtensionId,
            OperationOwner.Create<GetReadinessRequest, GetReadinessResponse>(
                OperationIds.HealthGetReadiness,
                GetReadinessAsync));
        registry.Register(
            OperationsModule.ExtensionId,
            OperationOwner.Create<GetStorageHealthRequest, GetStorageHealthResponse>(
                OperationIds.HealthGetStorage,
                GetStorageHealthAsync));
        registry.Register(
            OperationsModule.ExtensionId,
            OperationOwner.Create<GetDiagnosticsRequest, GetDiagnosticsResponse>(
                OperationIds.DiagnosticsGet,
                GetDiagnosticsAsync));
        registry.Register(
            OperationsModule.ExtensionId,
            OperationOwner.Create<CreateBackupRequest, CreateBackupResponse>(
                OperationIds.BackupCreate,
                CreateBackupAsync));
        registry.Register(
            OperationsModule.ExtensionId,
            OperationOwner.Create<RestoreBackupRequest, RestoreBackupResponse>(
                OperationIds.RestoreExecute,
                RestoreBackupAsync));
    }

    private async ValueTask<OperationResponse<GetLivenessResponse>> GetLivenessAsync(
        GetLivenessRequest request,
        CancellationToken token)
    {
        var report = await query.GetLivenessAsync(token);
        var response = new GetLivenessResponse(report.Status, report.Mode);
        return OperationResponse<GetLivenessResponse>.Success(
            response,
            new OperationResult(
                OperationResultStatus.Ok,
                new OperationDocumentBody(response)));
    }

    private async ValueTask<OperationResponse<GetReadinessResponse>> GetReadinessAsync(
        GetReadinessRequest request,
        CancellationToken token)
    {
        var report = await query.GetReadinessAsync(token);
        var response = new GetReadinessResponse(report.Status, report.Dependency, report.Ready);
        return OperationResponse<GetReadinessResponse>.Success(
            response,
            new OperationResult(
                report.Ready ? OperationResultStatus.Ok : OperationResultStatus.Unavailable,
                new OperationDocumentBody(new
                {
                    status = report.Status,
                    dependency = report.Dependency
                })));
    }

    private async ValueTask<OperationResponse<GetStorageHealthResponse>> GetStorageHealthAsync(
        GetStorageHealthRequest request,
        CancellationToken token)
    {
        var report = await query.GetStorageHealthAsync(token);
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
        return OperationResponse<GetStorageHealthResponse>.Success(
            response,
            new OperationResult(
                OperationResultStatus.Ok,
                new OperationDocumentBody(report)));
    }

    private async ValueTask<OperationResponse<GetDiagnosticsResponse>> GetDiagnosticsAsync(
        GetDiagnosticsRequest request,
        CancellationToken token)
    {
        var report = await query.GetDiagnosticsAsync(token);
        var response = new GetDiagnosticsResponse(
            report.RequestCount,
            report.FailedRequestCount,
            report.PublishedPackageCount,
            report.StorageFailureCount);
        return OperationResponse<GetDiagnosticsResponse>.Success(
            response,
            new OperationResult(
                OperationResultStatus.Ok,
                new OperationDocumentBody(response)));
    }

    private async ValueTask<OperationResponse<CreateBackupResponse>> CreateBackupAsync(
        CreateBackupRequest request,
        CancellationToken token)
    {
        var manifest = await backup.CreateAsync(
            request.Destination,
            request.RequestedBy,
            token);
        return manifest is null
            ? OperationResponse<CreateBackupResponse>.Failure(
                OperationErrors.Unavailable("Backups require durable storage."))
            : OperationResponse<CreateBackupResponse>.Success(new CreateBackupResponse(manifest));
    }

    private async ValueTask<OperationResponse<RestoreBackupResponse>> RestoreBackupAsync(
        RestoreBackupRequest request,
        CancellationToken token)
    {
        var manifest = await restore.RestoreAsync(request.Source, request.RequestedBy, token);
        return manifest is null
            ? OperationResponse<RestoreBackupResponse>.Failure(
                OperationErrors.Unavailable("Restores require durable storage."))
            : OperationResponse<RestoreBackupResponse>.Success(
                new RestoreBackupResponse(manifest));
    }
}
