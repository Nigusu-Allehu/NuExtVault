using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Requests;
using NuGet.Versioning;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Test-control owners. They wrap the existing package, fault, and request stores.
/// </summary>
internal sealed class ControlOperations(
    IPackageStore store,
    PackageSupplyChainService supplyChain,
    FaultRuleStore faults,
    RequestRecorder requests,
    ServerDiagnostics diagnostics,
    PackageTransferLimits limits)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<GetControlStateRequest, GetControlStateResponse>(
                OperationIds.ControlGetState,
                GetStateAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<ResetControlStateRequest, ResetControlStateResponse>(
                OperationIds.ControlReset,
                ResetAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<GetControlPackagesRequest, GetControlPackagesResponse>(
                OperationIds.ControlGetPackages,
                GetPackagesAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<AddControlPackageRequest, AddControlPackageResponse>(
                OperationIds.ControlAddPackage,
                AddPackageAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<DeleteControlPackageRequest, DeleteControlPackageResponse>(
                OperationIds.ControlDeletePackage,
                DeletePackageAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<RelistControlPackageRequest, RelistControlPackageResponse>(
                OperationIds.ControlRelistPackage,
                RelistPackageAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<UnlistControlPackageRequest, UnlistControlPackageResponse>(
                OperationIds.ControlUnlistPackage,
                UnlistPackageAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<UpdatePackageMetadataRequest, UpdatePackageMetadataResponse>(
                OperationIds.ControlUpdatePackageMetadata,
                UpdateMetadataAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<GetRequestsRequest, GetRequestsResponse>(
                OperationIds.ControlGetRequests,
                GetRequestsAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<ClearRequestsRequest, ClearRequestsResponse>(
                OperationIds.ControlClearRequests,
                ClearRequestsAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<GetFaultsRequest, GetFaultsResponse>(
                OperationIds.ControlGetFaults,
                GetFaultsAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<AddFaultRequest, AddFaultResponse>(
                OperationIds.ControlAddFault,
                AddFaultAsync));
        builder.Register(
            BuiltInExtensionIds.TestControl,
            new DelegateOperationOwner<ClearFaultsRequest, ClearFaultsResponse>(
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

    private async ValueTask<OperationResponse<GetControlStateResponse>> GetStateAsync(
        GetControlStateRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var packages = await store.GetAllAsync(token);
        var response = new GetControlStateResponse(
            packages.Count,
            faults.GetAll().Count,
            faults.Capacity,
            requests.GetAll().Count,
            requests.Capacity,
            requests.EvictedCount);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(new
            {
                packageCount = response.PackageCount,
                faultCount = response.FaultCount,
                faultCapacity = response.FaultCapacity,
                requestCount = response.RequestCount,
                requestCapacity = response.RequestCapacity,
                evictedRequestCount = response.EvictedRequestCount
            })));
        return OperationResponse<GetControlStateResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<ResetControlStateResponse>> ResetAsync(
        ResetControlStateRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        await supplyChain.ResetAsync(token);
        faults.Reset();
        requests.Reset();
        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<ResetControlStateResponse>.Success(new ResetControlStateResponse());
    }

    private async ValueTask<OperationResponse<GetControlPackagesResponse>> GetPackagesAsync(
        GetControlPackagesRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var response = new GetControlPackagesResponse(
            [.. (await store.GetAllAsync(token)).Select(CreateSummary)]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(response.Packages.Select(RenderSummary).ToArray())));
        return OperationResponse<GetControlPackagesResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<AddControlPackageResponse>> AddPackageAsync(
        AddControlPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var content = context.Content.Resolve(request.Content);
        TestPackage? package = null;
        try
        {
            package = await TestPackage.FromStreamAsync(
                content.Stream ?? new MemoryStream(content.Bytes!.Value.ToArray(), writable: false),
                limits,
                cancellationToken: token);
            await supplyChain.AddAsync(package, token);
            diagnostics.RecordPackagePublished();
            var summary = CreateSummary(package);
            package = null;
            context.Complete(new OperationHttpResult(
                StatusCodes.Status201Created,
                new JsonResponseBody(RenderSummary(summary)),
                $"/__test/packages/{Uri.EscapeDataString(summary.Package.Id)}/" +
                $"{summary.Package.Version}"));
            return OperationResponse<AddControlPackageResponse>.Success(
                new AddControlPackageResponse(summary));
        }
        finally
        {
            package?.Dispose();
        }
    }

    private async ValueTask<OperationResponse<DeleteControlPackageResponse>> DeletePackageAsync(
        DeleteControlPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        if (!await store.DeleteAsync(request.Package.Id, request.Package.Version, token))
        {
            return OperationResponse<DeleteControlPackageResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<DeleteControlPackageResponse>.Success(
            new DeleteControlPackageResponse(request.Package));
    }

    private async ValueTask<OperationResponse<RelistControlPackageResponse>> RelistPackageAsync(
        RelistControlPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        if (!await store.SetListedAsync(request.Package.Id, request.Package.Version, true, token))
        {
            return OperationResponse<RelistControlPackageResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<RelistControlPackageResponse>.Success(
            new RelistControlPackageResponse(request.Package));
    }

    private async ValueTask<OperationResponse<UnlistControlPackageResponse>> UnlistPackageAsync(
        UnlistControlPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        if (!await store.SetListedAsync(request.Package.Id, request.Package.Version, false, token))
        {
            return OperationResponse<UnlistControlPackageResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<UnlistControlPackageResponse>.Success(
            new UnlistControlPackageResponse(request.Package));
    }

    private async ValueTask<OperationResponse<UpdatePackageMetadataResponse>> UpdateMetadataAsync(
        UpdatePackageMetadataRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var validationError = Validate(request.Metadata);
        if (validationError is not null)
        {
            return OperationResponse<UpdatePackageMetadataResponse>.Failure(
                OperationErrorPolicy.InvalidRequest(validationError));
        }

        var metadata = new PackageRepositoryMetadata(
            [.. request.Metadata.Owners],
            request.Metadata.Downloads,
            request.Metadata.Verified,
            request.Metadata.Deprecation is { } deprecation
                ? new PackageDeprecation(
                    [.. deprecation.Reasons],
                    deprecation.Message!,
                    deprecation.AlternatePackage is { } alternate
                        ? new AlternatePackage(alternate.Id, alternate.Range)
                        : null)
                : null);
        if (!await store.SetRepositoryMetadataAsync(
                request.Package.Id,
                request.Package.Version,
                metadata,
                token))
        {
            return OperationResponse<UpdatePackageMetadataResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<UpdatePackageMetadataResponse>.Success(
            new UpdatePackageMetadataResponse(request.Package));
    }

    private ValueTask<OperationResponse<GetRequestsResponse>> GetRequestsAsync(
        GetRequestsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var records = requests.GetAll();
        var response = new GetRequestsResponse(
            [
                .. records.Select(record => new RequestRecordDocument(
                    record.Sequence,
                    record.Timestamp,
                    record.Method,
                    record.Path,
                    record.StatusCode,
                    record.DurationMilliseconds,
                    record.FaultRuleId,
                    record.AuthenticatedUser))
            ]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(records)));
        return ValueTask.FromResult(OperationResponse<GetRequestsResponse>.Success(response));
    }

    private ValueTask<OperationResponse<ClearRequestsResponse>> ClearRequestsAsync(
        ClearRequestsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        requests.Reset();
        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return ValueTask.FromResult(
            OperationResponse<ClearRequestsResponse>.Success(new ClearRequestsResponse()));
    }

    private ValueTask<OperationResponse<GetFaultsResponse>> GetFaultsAsync(
        GetFaultsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rules = faults.GetAll();
        var response = new GetFaultsResponse([.. rules.Select(CreateFaultDocument)]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(rules)));
        return ValueTask.FromResult(OperationResponse<GetFaultsResponse>.Success(response));
    }

    private ValueTask<OperationResponse<AddFaultResponse>> AddFaultAsync(
        AddFaultRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rule = CreateFaultRule(request.Fault);
        try
        {
            faults.Add(rule);
        }
        catch (FaultRuleStore.FaultRuleConflictException exception)
        {
            return ValueTask.FromResult(
                OperationResponse<AddFaultResponse>.Failure(
                    OperationErrorPolicy.Conflict(exception.Message)));
        }

        context.Complete(new OperationHttpResult(
            StatusCodes.Status201Created,
            new JsonResponseBody(rule),
            $"/__test/faults/{Uri.EscapeDataString(rule.Id)}"));
        return ValueTask.FromResult(
            OperationResponse<AddFaultResponse>.Success(
                new AddFaultResponse(CreateFaultDocument(rule))));
    }

    private ValueTask<OperationResponse<ClearFaultsResponse>> ClearFaultsAsync(
        ClearFaultsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        faults.Reset();
        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return ValueTask.FromResult(
            OperationResponse<ClearFaultsResponse>.Success(new ClearFaultsResponse()));
    }

    internal static FaultRuleDocument CreateFaultDocument(FaultRule rule) =>
        new(
            rule.Id,
            rule.Method ?? string.Empty,
            rule.PathContains ?? string.Empty,
            (int)rule.StatusCode,
            (long)rule.Delay.TotalMilliseconds,
            rule.RemainingMatches);

    internal static FaultRule CreateFaultRule(FaultRuleDocument document) =>
        new(
            document.Id,
            string.IsNullOrEmpty(document.Method) ? null : document.Method,
            string.IsNullOrEmpty(document.RoutePattern) ? null : document.RoutePattern,
            (System.Net.HttpStatusCode)document.StatusCode,
            document.RemainingMatches ?? 0,
            TimeSpan.FromMilliseconds(document.DelayMilliseconds));

    private static PackageSummaryDocument CreateSummary(TestPackage package) =>
        new(
            new PackageIdentity(package.Identity.Id, package.NormalizedVersion),
            package.IsListed,
            package.Published);

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
        OperationErrorPolicy.NotFound(
            $"Package '{package.Id}' version '{package.Version}' does not exist.");
}
