using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.Versioning;

namespace NuGet.TestServer.Extensions.Control;

/// <summary>
/// Test-control owners. They reach kernel state only through action-scoped
/// capabilities, and they describe responses with the transport-neutral rendering
/// contract. They never see an execution context, an HTTP request context, a status
/// code, or a serializer.
/// </summary>
internal sealed class ControlOperations(
    IPackageControlCapability packages,
    IKernelInstrumentationControlCapability instrumentation)
{
    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<GetControlStateRequest, GetControlStateResponse>(
                OperationIds.ControlGetState,
                GetStateAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<ResetControlStateRequest, ResetControlStateResponse>(
                OperationIds.ControlReset,
                ResetAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<GetControlPackagesRequest, GetControlPackagesResponse>(
                OperationIds.ControlGetPackages,
                GetPackagesAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<AddControlPackageRequest, AddControlPackageResponse>(
                OperationIds.ControlAddPackage,
                AddPackageAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<DeleteControlPackageRequest, DeleteControlPackageResponse>(
                OperationIds.ControlDeletePackage,
                DeletePackageAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<RelistControlPackageRequest, RelistControlPackageResponse>(
                OperationIds.ControlRelistPackage,
                RelistPackageAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<UnlistControlPackageRequest, UnlistControlPackageResponse>(
                OperationIds.ControlUnlistPackage,
                UnlistPackageAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<UpdatePackageMetadataRequest, UpdatePackageMetadataResponse>(
                OperationIds.ControlUpdatePackageMetadata,
                UpdateMetadataAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<GetRequestsRequest, GetRequestsResponse>(
                OperationIds.ControlGetRequests,
                GetRequestsAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<ClearRequestsRequest, ClearRequestsResponse>(
                OperationIds.ControlClearRequests,
                ClearRequestsAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<GetFaultsRequest, GetFaultsResponse>(
                OperationIds.ControlGetFaults,
                GetFaultsAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<AddFaultRequest, AddFaultResponse>(
                OperationIds.ControlAddFault,
                AddFaultAsync));
        registry.Register(
            BuiltInExtensionIds.TestControl,
            OperationOwner.Create<ClearFaultsRequest, ClearFaultsResponse>(
                OperationIds.ControlClearFaults,
                ClearFaultsAsync));
    }

    internal static object RenderSummary(PackageSummaryDocument summary) => new
    {
        id = summary.Package.Id,
        version = summary.Package.Version,
        listed = summary.Listed,
        published = summary.Published
    };

    /// <summary>
    /// The legacy fault-rule wire shape. The typed contract stays canonical; this is
    /// only the document the kernel serializes for compatibility.
    /// </summary>
    internal static object RenderFault(FaultRuleDocument fault) => new
    {
        id = fault.Id,
        method = string.IsNullOrEmpty(fault.Method) ? null : fault.Method,
        pathContains = string.IsNullOrEmpty(fault.RoutePattern) ? null : fault.RoutePattern,
        statusCode = fault.StatusCode,
        remainingMatches = fault.RemainingMatches ?? 0,
        delay = TimeSpan.FromMilliseconds(fault.DelayMilliseconds)
    };

    internal static object RenderRequest(RequestRecordDocument record) => new
    {
        sequence = record.Sequence,
        timestamp = record.OccurredAt,
        method = record.Method,
        path = record.Route,
        statusCode = record.StatusCode,
        durationMilliseconds = record.ElapsedMilliseconds,
        faultRuleId = record.FaultRuleId,
        authenticatedUser = record.Identity
    };

    private async ValueTask<OperationResponse<GetControlStateResponse>> GetStateAsync(
        GetControlStateRequest request,
        CancellationToken token)
    {
        var storedPackages = await packages.GetAllAsync(token);
        var faultRules = await instrumentation.GetFaultsAsync(token);
        var requestRecords = await instrumentation.GetRequestsAsync(token);
        var response = new GetControlStateResponse(
            storedPackages.Count,
            faultRules.Count,
            instrumentation.FaultCapacity,
            requestRecords.Count,
            instrumentation.RequestCapacity,
            instrumentation.EvictedRequestCount);
        return OperationResponse<GetControlStateResponse>.Success(
            response,
            OperationResult.Ok(new
            {
                packageCount = response.PackageCount,
                faultCount = response.FaultCount,
                faultCapacity = response.FaultCapacity,
                requestCount = response.RequestCount,
                requestCapacity = response.RequestCapacity,
                evictedRequestCount = response.EvictedRequestCount
            }));
    }

    private async ValueTask<OperationResponse<ResetControlStateResponse>> ResetAsync(
        ResetControlStateRequest request,
        CancellationToken token)
    {
        await packages.ResetAsync(token);
        await instrumentation.ClearFaultsAsync(token);
        await instrumentation.ClearRequestsAsync(token);
        return OperationResponse<ResetControlStateResponse>.Success(
            new ResetControlStateResponse(),
            OperationResult.NoContent());
    }

    private async ValueTask<OperationResponse<GetControlPackagesResponse>> GetPackagesAsync(
        GetControlPackagesRequest request,
        CancellationToken token)
    {
        var response = new GetControlPackagesResponse([.. await packages.GetAllAsync(token)]);
        return OperationResponse<GetControlPackagesResponse>.Success(
            response,
            OperationResult.Ok(response.Packages.Select(RenderSummary).ToArray()));
    }

    private async ValueTask<OperationResponse<AddControlPackageResponse>> AddPackageAsync(
        AddControlPackageRequest request,
        CancellationToken token)
    {
        var summary = await packages.AddContentAsync(request.Content, token);
        return OperationResponse<AddControlPackageResponse>.Success(
            new AddControlPackageResponse(summary),
            OperationResult.Created(
                RenderSummary(summary),
                $"/__test/packages/{Uri.EscapeDataString(summary.Package.Id)}/" +
                $"{summary.Package.Version}"));
    }

    private async ValueTask<OperationResponse<DeleteControlPackageResponse>> DeletePackageAsync(
        DeleteControlPackageRequest request,
        CancellationToken token)
    {
        if (!await packages.DeleteAsync(request.Package.Id, request.Package.Version, token))
        {
            return OperationResponse<DeleteControlPackageResponse>.Failure(NotFound(request.Package));
        }

        return OperationResponse<DeleteControlPackageResponse>.Success(
            new DeleteControlPackageResponse(request.Package),
            OperationResult.NoContent());
    }

    private async ValueTask<OperationResponse<RelistControlPackageResponse>> RelistPackageAsync(
        RelistControlPackageRequest request,
        CancellationToken token)
    {
        if (!await packages.SetListedAsync(
                request.Package.Id,
                request.Package.Version,
                true,
                token))
        {
            return OperationResponse<RelistControlPackageResponse>.Failure(NotFound(request.Package));
        }

        return OperationResponse<RelistControlPackageResponse>.Success(
            new RelistControlPackageResponse(request.Package),
            OperationResult.NoContent());
    }

    private async ValueTask<OperationResponse<UnlistControlPackageResponse>> UnlistPackageAsync(
        UnlistControlPackageRequest request,
        CancellationToken token)
    {
        if (!await packages.SetListedAsync(
                request.Package.Id,
                request.Package.Version,
                false,
                token))
        {
            return OperationResponse<UnlistControlPackageResponse>.Failure(NotFound(request.Package));
        }

        return OperationResponse<UnlistControlPackageResponse>.Success(
            new UnlistControlPackageResponse(request.Package),
            OperationResult.NoContent());
    }

    private async ValueTask<OperationResponse<UpdatePackageMetadataResponse>> UpdateMetadataAsync(
        UpdatePackageMetadataRequest request,
        CancellationToken token)
    {
        var validationError = Validate(request.Metadata);
        if (validationError is not null)
        {
            return OperationResponse<UpdatePackageMetadataResponse>.Failure(
                OperationErrors.InvalidRequest(validationError));
        }

        if (!await packages.SetRepositoryMetadataAsync(
                request.Package.Id,
                request.Package.Version,
                request.Metadata,
                token))
        {
            return OperationResponse<UpdatePackageMetadataResponse>.Failure(NotFound(request.Package));
        }

        return OperationResponse<UpdatePackageMetadataResponse>.Success(
            new UpdatePackageMetadataResponse(request.Package),
            OperationResult.NoContent());
    }

    private async ValueTask<OperationResponse<GetRequestsResponse>> GetRequestsAsync(
        GetRequestsRequest request,
        CancellationToken token)
    {
        var response = new GetRequestsResponse([.. await instrumentation.GetRequestsAsync(token)]);
        return OperationResponse<GetRequestsResponse>.Success(
            response,
            OperationResult.Ok(response.Requests.Select(RenderRequest).ToArray()));
    }

    private async ValueTask<OperationResponse<ClearRequestsResponse>> ClearRequestsAsync(
        ClearRequestsRequest request,
        CancellationToken token)
    {
        await instrumentation.ClearRequestsAsync(token);
        return OperationResponse<ClearRequestsResponse>.Success(
            new ClearRequestsResponse(),
            OperationResult.NoContent());
    }

    private async ValueTask<OperationResponse<GetFaultsResponse>> GetFaultsAsync(
        GetFaultsRequest request,
        CancellationToken token)
    {
        var response = new GetFaultsResponse([.. await instrumentation.GetFaultsAsync(token)]);
        return OperationResponse<GetFaultsResponse>.Success(
            response,
            OperationResult.Ok(response.Faults.Select(RenderFault).ToArray()));
    }

    private async ValueTask<OperationResponse<AddFaultResponse>> AddFaultAsync(
        AddFaultRequest request,
        CancellationToken token)
    {
        var conflict = await instrumentation.TryAddFaultAsync(request.Fault, token);
        if (conflict is not null)
        {
            return OperationResponse<AddFaultResponse>.Failure(
                OperationErrors.Conflict(conflict));
        }

        return OperationResponse<AddFaultResponse>.Success(
            new AddFaultResponse(request.Fault),
            OperationResult.Created(
                RenderFault(request.Fault),
                $"/__test/faults/{Uri.EscapeDataString(request.Fault.Id)}"));
    }

    private async ValueTask<OperationResponse<ClearFaultsResponse>> ClearFaultsAsync(
        ClearFaultsRequest request,
        CancellationToken token)
    {
        await instrumentation.ClearFaultsAsync(token);
        return OperationResponse<ClearFaultsResponse>.Success(
            new ClearFaultsResponse(),
            OperationResult.NoContent());
    }

    private static string? Validate(PackageRepositoryMetadataDocument metadata)
    {
        if (metadata.Downloads < 0)
        {
            return "Downloads cannot be negative.";
        }

        if (metadata.Owners.IsDefault || metadata.Owners.Any(string.IsNullOrWhiteSpace))
        {
            return "Owners cannot contain empty values.";
        }

        if (metadata.Deprecation is not { } deprecation)
        {
            return null;
        }

        string[] validReasons = ["Legacy", "CriticalBugs", "Other"];
        if (deprecation.Reasons.IsDefault ||
            deprecation.Reasons.Length == 0 ||
            deprecation.Reasons.Any(reason =>
                !validReasons.Contains(reason, StringComparer.OrdinalIgnoreCase)))
        {
            return "Deprecation reasons must be Legacy, CriticalBugs, or Other.";
        }

        if (deprecation.AlternatePackage is { } alternate &&
            (string.IsNullOrWhiteSpace(alternate.Id) ||
             !VersionRange.TryParse(alternate.Range, out _)))
        {
            return "The alternate package requires an ID and valid version range.";
        }

        return null;
    }

    private static OperationError NotFound(PackageIdentity package) =>
        OperationErrors.NotFound(
            $"Package '{package.Id}' version '{package.Version}' does not exist.");
}
