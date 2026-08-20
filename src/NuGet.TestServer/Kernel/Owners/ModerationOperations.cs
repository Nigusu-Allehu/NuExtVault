using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Moderation owners. They wrap the existing supply-chain service.
/// </summary>
internal sealed class ModerationOperations(IModerationCapability moderation)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.SupplyChain,
            new DelegateOperationOwner<ModeratePackageRequest, ModeratePackageResponse>(
                OperationIds.ModerationModerate,
                ModerateAsync));
        builder.Register(
            BuiltInExtensionIds.SupplyChain,
            new DelegateOperationOwner<GetModerationAuditRequest, GetModerationAuditResponse>(
                OperationIds.ModerationGetAudit,
                GetAuditAsync));
        builder.Register(
            BuiltInExtensionIds.SupplyChain,
            new DelegateOperationOwner<GetPackageValidationsRequest, GetPackageValidationsResponse>(
                OperationIds.ModerationGetValidations,
                GetValidationsAsync));
    }

    private async ValueTask<OperationResponse<ModeratePackageResponse>> ModerateAsync(
        ModeratePackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var applied = request.Action == ModerationAction.Delete
            ? await moderation.DeleteControlledAsync(
                request.Package.Id,
                request.Package.Version,
                request.Actor,
                request.Reason,
                token)
            : await moderation.ModerateAsync(
                request.Package.Id,
                request.Package.Version,
                MapState(request.Action),
                request.Actor,
                request.Reason,
                token);
        if (!applied)
        {
            return OperationResponse<ModeratePackageResponse>.Failure(
                OperationErrorPolicy.NotFound(
                    $"Package '{request.Package.Id}' version " +
                    $"'{request.Package.Version}' does not exist."));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<ModeratePackageResponse>.Success(
            new ModeratePackageResponse(request.Package, request.Action));
    }

    private async ValueTask<OperationResponse<GetModerationAuditResponse>> GetAuditAsync(
        GetModerationAuditRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var history = await moderation.GetAuditHistoryAsync(token);
        var filtered = history
            .Where(entry => entry.Sequence > (request.AfterSequence ?? 0))
            .Take(request.Take <= 0 ? history.Count : request.Take)
            .ToArray();

        // The typed contract models moderation transitions. The supply-chain audit
        // trail additionally records publication and recovery events, which the
        // current route keeps returning verbatim until moderation is extracted.
        var response = new GetModerationAuditResponse(
            [
                .. filtered
                    .Where(entry => TryMapAction(entry, out _))
                    .Select(entry =>
                    {
                        TryMapAction(entry, out var action);
                        return new ModerationAuditDocument(
                            entry.Sequence,
                            new PackageIdentity(
                                entry.PackageId ?? string.Empty,
                                entry.PackageVersion ?? string.Empty),
                            action,
                            entry.Actor,
                            entry.Detail,
                            entry.Timestamp);
                    })
            ]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(history)));
        return OperationResponse<GetModerationAuditResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<GetPackageValidationsResponse>> GetValidationsAsync(
        GetPackageValidationsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var validations = await moderation.GetValidationResultsAsync(
            request.Package.Id,
            request.Package.Version,
            token);
        var response = new GetPackageValidationsResponse(
            [
                .. validations.Select(validation => new PackageValidationDocument(
                    validation.Validator,
                    validation.Outcome,
                    validation.Detail))
            ]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(validations)));
        return OperationResponse<GetPackageValidationsResponse>.Success(response);
    }

    private static PackageModerationState MapState(ModerationAction action) => action switch
    {
        ModerationAction.Approve => PackageModerationState.Published,
        ModerationAction.Reject => PackageModerationState.Rejected,
        ModerationAction.Quarantine => PackageModerationState.Quarantined,
        _ => PackageModerationState.Deleted
    };

    private static bool TryMapAction(PackageSupplyChainAudit entry, out ModerationAction action)
    {
        action = ModerationAction.Approve;
        if (!string.Equals(entry.Action, "moderate", StringComparison.Ordinal))
        {
            return false;
        }

        switch (entry.Result)
        {
            case "published":
                action = ModerationAction.Approve;
                return true;
            case "rejected":
                action = ModerationAction.Reject;
                return true;
            case "quarantined":
                action = ModerationAction.Quarantine;
                return true;
            case "deleted":
                action = ModerationAction.Delete;
                return true;
            default:
                return false;
        }
    }
}
