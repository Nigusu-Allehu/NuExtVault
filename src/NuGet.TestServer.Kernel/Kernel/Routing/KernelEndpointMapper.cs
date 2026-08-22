using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Packages;
using NuGet.Versioning;

namespace NuGet.TestServer.Kernel.Routing;

/// <summary>
/// Kernel-owned package version normalization for route values. Package identity rules
/// never leave the kernel.
/// </summary>
internal static class KernelPackageVersions
{
    public static string Normalize(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return NuGetVersion.TryParse(version, out var parsed)
            ? TestPackage.NormalizeVersion(parsed)
            : version.ToLowerInvariant();
    }
}

/// <summary>
/// The only place in the server that creates ASP.NET endpoints. Every active route is
/// generated here from the frozen kernel route table; no feature code maps routes.
/// </summary>
internal static class KernelEndpointMapper
{
    public static void Map(WebApplication app, KernelRouteTable routes)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(routes);

        foreach (var endpoint in routes.Endpoints)
        {
            var route = endpoint;
            app.MapMethods(
                    route.Descriptor.PathTemplate,
                    route.Methods,
                    (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                        gateway.ExecuteEndpointAsync(route, context, token))
                .WithMetadata(route.Access)
                .WithMetadata(new OperationRouteMetadata(
                    [.. route.Descriptor.Operations.Select(operation => operation.OperationId)]))
                .WithMetadata(route);
        }
    }
}

/// <summary>
/// The HTTP implementation of the transport-neutral request surface. It is created by
/// the gateway for one request and never handed an owner or descriptor more than the
/// declared binding surface.
/// </summary>
internal sealed class HttpEndpointRequest(
    HttpContext context,
    OperationExecutionContext execution,
    EndpointLimits limits) : EndpointRequest
{
    public override string Method => context.Request.Method.ToUpperInvariant();

    public override string? ContentType => context.Request.ContentType;

    public override long? ContentLength => context.Request.ContentLength;

    public override bool HasFormContent => context.Request.HasFormContentType;

    public override EndpointLimits Limits { get; } = limits;

    public override EndpointCaller Caller { get; } = new(
        execution.Authorization.HasIdentity,
        execution.Authorization.IdentityName,
        execution.Authorization.IsAdministrator);

    public override string GetRoute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return context.Request.RouteValues.TryGetValue(name, out var value)
            ? value?.ToString() ?? string.Empty
            : throw new InvalidOperationException(
                $"Route parameter '{name}' is not declared by the matched route.");
    }

    public override string? GetQuery(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return context.Request.Query.TryGetValue(name, out var values)
            ? values.ToString()
            : null;
    }

    public override string GetNormalizedPackageVersion(string name) =>
        KernelPackageVersions.Normalize(GetRoute(name));

    public override string? GetHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
    }

    public override async ValueTask<BoundedDocument> ReadBoundedBodyAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (context.Request.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new OperationBindingException(new OperationResult(
                OperationResultStatus.PayloadTooLarge,
                new OperationProblemBody("The request body exceeds the declared route limit.")));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(81920, maximumBytes)];
        var total = 0L;
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new OperationBindingException(new OperationResult(
                    OperationResultStatus.PayloadTooLarge,
                    new OperationProblemBody(
                        "The request body exceeds the declared route limit.")));
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return new BoundedDocument(
            buffer.ToArray(),
            maximumBytes,
            context.Request.ContentType ?? "application/octet-stream");
    }

    public override int? GetQueryInt32(string name)
    {
        var value = GetQuery(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw BindingFailure(
                OperationResultStatus.InvalidRequest,
                $"Failed to bind parameter \"{name}\" from \"{value}\".");
    }

    public override bool? GetQueryBoolean(string name)
    {
        var value = GetQuery(name);
        if (value is null)
        {
            return null;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw BindingFailure(
                OperationResultStatus.InvalidRequest,
                $"Failed to bind parameter \"{name}\" from \"{value}\".");
    }

    public override StreamHandle BindBodyStream()
    {
        var declaredLength = context.Request.ContentLength ?? 0;
        return execution.Content.RegisterStream(
            context.Request.Body,
            context.Request.ContentType ?? "application/octet-stream",
            declaredLength,
            maximumLength: Limits.MaxRequestBytes > 0
                ? Limits.MaxRequestBytes
                : Math.Max(1, declaredLength));
    }

    public override async ValueTask<StreamHandle> BindUploadAsync(
        string missingFileDetail,
        CancellationToken cancellationToken)
    {
        if (!HasFormContent)
        {
            return BindBodyStream();
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new OperationBindingException(new OperationResult(
                OperationResultStatus.PayloadTooLarge,
                new OperationProblemBody(exception.Message)));
        }

        var file = form.Files.FirstOrDefault();
        if (file is null)
        {
            throw new OperationBindingException(new OperationResult(
                OperationResultStatus.InternalError,
                new OperationProblemBody(missingFileDetail)));
        }

        return execution.Content.RegisterStream(
            file.OpenReadStream(),
            file.ContentType ?? "application/octet-stream",
            file.Length,
            maximumLength: Limits.MaxRequestBytes > 0
                ? Limits.MaxRequestBytes
                : Math.Max(1, file.Length));
    }

    public override StreamHandle RegisterContent(ReadOnlyMemory<byte> content, string contentType) =>
        execution.Content.RegisterBytes(content, contentType);

    public override async ValueTask<TBody> ReadRequiredJsonAsync<TBody>(
        CancellationToken cancellationToken)
    {
        if (!HasJsonContentType())
        {
            throw BindingFailure(
                OperationResultStatus.UnsupportedMediaType,
                "Expected a supported JSON media type but got \"" +
                (ContentType ?? string.Empty) + "\".");
        }

        TBody? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<TBody>(cancellationToken);
        }
        catch (JsonException exception)
        {
            throw BindingFailure(
                OperationResultStatus.InvalidRequest,
                "Failed to read parameter from the request body as JSON.",
                exception);
        }

        return body ?? throw BindingFailure(
            OperationResultStatus.InvalidRequest,
            "Implicit body inferred for parameter but no body was provided.");
    }

    public override async ValueTask<TBody?> ReadOptionalJsonAsync<TBody>(
        CancellationToken cancellationToken)
        where TBody : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<TBody>(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void LimitRequestBody(long maximumBytes)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maximumBytes;
        }
    }

    private bool HasJsonContentType()
    {
        if (ContentType is not { } contentType)
        {
            return false;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static OperationBindingException BindingFailure(
        OperationResultStatus status,
        string detail,
        Exception? innerException = null) =>
        new(
            new OperationResult(status, new OperationProblemBody(detail)),
            innerException);
}
