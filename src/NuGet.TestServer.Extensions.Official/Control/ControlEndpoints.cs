using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Control;

/// <summary>
/// Test-control endpoint descriptors. The control extension owns them; the kernel
/// generates the routes, enforces the declared access policy and limits, and dispatches
/// the declared operations.
/// </summary>
internal static class ControlEndpoints
{
    private const long LegacyJsonPackageLimit = 4L * 1024 * 1024;

    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
    [
        Simple<GetControlStateRequest, GetControlStateResponse>(
            "control.state",
            "GET",
            "/__test/state",
            OperationIds.ControlGetState,
            _ => new GetControlStateRequest()),
        Simple<ResetControlStateRequest, ResetControlStateResponse>(
            "control.reset",
            "POST",
            "/__test/reset",
            OperationIds.ControlReset,
            _ => new ResetControlStateRequest()),
        Simple<GetControlPackagesRequest, GetControlPackagesResponse>(
            "control.packages.list",
            "GET",
            "/__test/packages",
            OperationIds.ControlGetPackages,
            _ => new GetControlPackagesRequest()),
        new()
        {
            Name = "control.packages.add",
            Methods = ["POST"],
            PathTemplate = "/__test/packages",
            Body = EndpointBodyBinding.Stream,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.PackageTransfer,
            Operations =
            [
                EndpointDescriptor.Operation<AddControlPackageRequest, AddControlPackageResponse>(
                    OperationIds.ControlAddPackage)
            ],
            Handler = EndpointHandler.Create(BindControlPackageAsync)
        },
        Identity<DeleteControlPackageRequest, DeleteControlPackageResponse>(
            "control.packages.delete",
            "DELETE",
            "/__test/packages/{id}/{version}",
            OperationIds.ControlDeletePackage,
            identity => new DeleteControlPackageRequest(identity)),
        Identity<RelistControlPackageRequest, RelistControlPackageResponse>(
            "control.packages.list-package",
            "POST",
            "/__test/packages/{id}/{version}/list",
            OperationIds.ControlRelistPackage,
            identity => new RelistControlPackageRequest(identity)),
        Identity<UnlistControlPackageRequest, UnlistControlPackageResponse>(
            "control.packages.unlist-package",
            "POST",
            "/__test/packages/{id}/{version}/unlist",
            OperationIds.ControlUnlistPackage,
            identity => new UnlistControlPackageRequest(identity)),
        new()
        {
            Name = "control.packages.metadata",
            Methods = ["PUT"],
            PathTemplate = "/__test/packages/{id}/{version}/metadata",
            RouteParameters =
            [
                new EndpointParameter("id"),
                new EndpointParameter("version")
            ],
            Body = EndpointBodyBinding.Json,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.BoundedBody(LegacyJsonPackageLimit),
            Operations =
            [
                EndpointDescriptor
                    .Operation<UpdatePackageMetadataRequest, UpdatePackageMetadataResponse>(
                        OperationIds.ControlUpdatePackageMetadata)
            ],
            Handler = EndpointHandler.Create(async (request, token) =>
                EndpointInvocation
                    .Operation<UpdatePackageMetadataRequest, UpdatePackageMetadataResponse>(
                        OperationIds.ControlUpdatePackageMetadata,
                        new UpdatePackageMetadataRequest(
                            ReadIdentity(request),
                            await request
                                .ReadRequiredJsonAsync<PackageRepositoryMetadataDocument>(
                                    token))))
        },
        Simple<GetRequestsRequest, GetRequestsResponse>(
            "control.requests.list",
            "GET",
            "/__test/requests",
            OperationIds.ControlGetRequests,
            _ => new GetRequestsRequest()),
        Simple<ClearRequestsRequest, ClearRequestsResponse>(
            "control.requests.clear",
            "DELETE",
            "/__test/requests",
            OperationIds.ControlClearRequests,
            _ => new ClearRequestsRequest()),
        Simple<GetFaultsRequest, GetFaultsResponse>(
            "control.faults.list",
            "GET",
            "/__test/faults",
            OperationIds.ControlGetFaults,
            _ => new GetFaultsRequest()),
        new()
        {
            Name = "control.faults.add",
            Methods = ["POST"],
            PathTemplate = "/__test/faults",
            Body = EndpointBodyBinding.Json,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.BoundedBody(LegacyJsonPackageLimit),
            Operations =
            [
                EndpointDescriptor.Operation<AddFaultRequest, AddFaultResponse>(
                    OperationIds.ControlAddFault)
            ],
            Handler = EndpointHandler.Create(async (request, token) =>
                EndpointInvocation.Operation<AddFaultRequest, AddFaultResponse>(
                    OperationIds.ControlAddFault,
                    new AddFaultRequest(CreateFaultDocument(
                        await request.ReadRequiredJsonAsync<ControlFaultRuleRequest>(token)))))
        },
        Simple<ClearFaultsRequest, ClearFaultsResponse>(
            "control.faults.clear",
            "DELETE",
            "/__test/faults",
            OperationIds.ControlClearFaults,
            _ => new ClearFaultsRequest())
    ];

    private static EndpointDescriptor Simple<TRequest, TResponse>(
        string name,
        string method,
        string path,
        string operationId,
        Func<EndpointRequest, TRequest> bind) =>
        new()
        {
            Name = name,
            Methods = [method],
            PathTemplate = path,
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.BodyFree,
            Operations = [EndpointDescriptor.Operation<TRequest, TResponse>(operationId)],
            Handler = EndpointHandler.Create<TRequest, TResponse>(operationId, bind)
        };

    private static EndpointDescriptor Identity<TRequest, TResponse>(
        string name,
        string method,
        string path,
        string operationId,
        Func<PackageIdentity, TRequest> bind) =>
        new()
        {
            Name = name,
            Methods = [method],
            PathTemplate = path,
            RouteParameters =
            [
                new EndpointParameter("id"),
                new EndpointParameter("version")
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.BodyFree,
            Operations = [EndpointDescriptor.Operation<TRequest, TResponse>(operationId)],
            Handler = EndpointHandler.Create<TRequest, TResponse>(
                operationId,
                request => bind(ReadIdentity(request)))
        };

    private static PackageIdentity ReadIdentity(EndpointRequest request) =>
        new(request.GetRoute("id"), request.GetRoute("version"));

    private static FaultRuleDocument CreateFaultDocument(ControlFaultRuleRequest rule) =>
        new(
            rule.Id,
            rule.Method ?? string.Empty,
            rule.PathContains ?? string.Empty,
            rule.StatusCode,
            (long)rule.Delay.TotalMilliseconds,
            rule.RemainingMatches);

    private static async ValueTask<EndpointInvocation> BindControlPackageAsync(
        EndpointRequest request,
        CancellationToken token)
    {
        if (request.ContentType?.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return EndpointInvocation.Operation<AddControlPackageRequest, AddControlPackageResponse>(
                OperationIds.ControlAddPackage,
                new AddControlPackageRequest(request.BindBodyStream()));
        }

        var legacyPackageLimit = Math.Min(
            request.Limits.MaxContentBytes,
            LegacyJsonPackageLimit);
        var maximumBase64Length = checked(((legacyPackageLimit + 2) / 3) * 4);
        var legacyRequestLimit = Math.Min(
            request.Limits.MaxRequestBytes,
            checked(maximumBase64Length + 1024));
        if (request.ContentLength > legacyRequestLimit)
        {
            throw LegacyLimitExceeded(legacyPackageLimit);
        }

        request.LimitRequestBody(legacyRequestLimit);
        var packageRequest = await request
            .ReadOptionalJsonAsync<PackageContentRequest>(token);
        if (packageRequest?.Content is null)
        {
            throw InvalidJson();
        }

        if (packageRequest.Content.Length > maximumBase64Length)
        {
            throw LegacyLimitExceeded(legacyPackageLimit);
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(packageRequest.Content);
        }
        catch (FormatException)
        {
            throw new OperationBindingException(new OperationResult(
                OperationResultStatus.InvalidRequest,
                new OperationProblemBody("Package content must be valid base64.")));
        }

        return EndpointInvocation.Operation<AddControlPackageRequest, AddControlPackageResponse>(
            OperationIds.ControlAddPackage,
            new AddControlPackageRequest(
                request.RegisterContent(content, "application/octet-stream")));
    }

    private static OperationBindingException LegacyLimitExceeded(long legacyPackageLimit) =>
        new(new OperationResult(
            OperationResultStatus.PayloadTooLarge,
            new OperationProblemBody(
                $"Legacy JSON control uploads are limited to {legacyPackageLimit} decoded " +
                "bytes. Use 'application/octet-stream' for larger packages.")));

    private static OperationBindingException InvalidJson() =>
        new(new OperationResult(
            OperationResultStatus.InvalidRequest,
            new OperationProblemBody(
                "The package request must contain valid JSON and base64 content.")));
}

/// <summary>
/// The legacy JSON control upload payload.
/// </summary>
internal sealed record PackageContentRequest(string? Content);

/// <summary>
/// The legacy fault-rule wire shape accepted by <c>POST /__test/faults</c>.
/// </summary>
internal sealed record ControlFaultRuleRequest(
    string Id,
    string? Method,
    string? PathContains,
    int StatusCode,
    int RemainingMatches,
    TimeSpan Delay);
