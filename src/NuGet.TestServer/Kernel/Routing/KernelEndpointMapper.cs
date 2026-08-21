using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel.Routing;

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

    public override string Path => context.Request.Path.Value ?? "/";

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
                StatusCodes.Status400BadRequest,
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
                StatusCodes.Status400BadRequest,
                $"Failed to bind parameter \"{name}\" from \"{value}\".");
    }

    public override StreamHandle BindBodyStream() =>
        execution.Content.RegisterStream(
            context.Request.Body,
            context.Request.ContentType ?? "application/octet-stream",
            context.Request.ContentLength ?? 0);

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
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status413PayloadTooLarge,
                new ProblemResponseBody(exception.Message)));
        }

        var file = form.Files.FirstOrDefault();
        if (file is null)
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status500InternalServerError,
                new ProblemResponseBody(missingFileDetail)));
        }

        return execution.Content.RegisterStream(
            file.OpenReadStream(),
            file.ContentType ?? "application/octet-stream",
            file.Length);
    }

    public override StreamHandle RegisterContent(ReadOnlyMemory<byte> content, string contentType) =>
        execution.Content.RegisterBytes(content, contentType);

    public override async ValueTask<TBody> ReadRequiredJsonAsync<TBody>(
        CancellationToken cancellationToken)
    {
        if (!HasJsonContentType())
        {
            throw BindingFailure(
                StatusCodes.Status415UnsupportedMediaType,
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
                StatusCodes.Status400BadRequest,
                "Failed to read parameter from the request body as JSON.",
                exception);
        }

        return body ?? throw BindingFailure(
            StatusCodes.Status400BadRequest,
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
        int statusCode,
        string detail,
        Exception? innerException = null) =>
        new(
            new OperationHttpResult(statusCode, new ProblemResponseBody(detail)),
            innerException);
}
