using System.Collections.Immutable;

namespace NuExtVault.Extensions.Sdk;

/// <summary>
/// The access policy an endpoint requires. It is transport-neutral; the kernel maps it
/// onto the concrete authentication requirement when it generates the route table.
/// </summary>
public enum EndpointAccessKind
{
    Unspecified = 0,
    Anonymous,
    Read,
    Write,
    Publish,
    Unlist,
    Delete,
    Admin,
    Control
}

/// <summary>
/// An endpoint access policy. Hosts that run with a production identity may require a
/// narrower scope than pre-production hosts for the same route.
/// </summary>
internal sealed record EndpointAccessPolicy(
    EndpointAccessKind Default,
    EndpointAccessKind? ProductionIdentity = null)
{
    public static EndpointAccessPolicy Unspecified { get; } = new(EndpointAccessKind.Unspecified);

    public static EndpointAccessPolicy Of(EndpointAccessKind kind) => new(kind);

    public EndpointAccessKind Resolve(bool hasProductionIdentity) =>
        hasProductionIdentity && ProductionIdentity is { } production ? production : Default;
}

/// <summary>
/// Whether the endpoint answers HEAD with the same handler and status as GET.
/// </summary>
internal enum EndpointHeadPolicy
{
    None = 0,
    MirrorsGet
}

internal enum EndpointBodyKind
{
    None = 0,
    BoundedBody,
    Stream
}

/// <summary>
/// How the kernel binds a request payload. Bounded bodies are read into a contract,
/// streams are handed to the owner as a kernel content handle and never buffered.
/// </summary>
internal sealed record EndpointBodyBinding(EndpointBodyKind Kind, string? MediaType = null)
{
    public static EndpointBodyBinding None { get; } = new(EndpointBodyKind.None);

    public static EndpointBodyBinding Stream { get; } = new(EndpointBodyKind.Stream);

    public static EndpointBodyBinding Json { get; } =
        new(EndpointBodyKind.BoundedBody, "application/json");

    public static EndpointBodyBinding Bounded(string? mediaType = null) =>
        new(EndpointBodyKind.BoundedBody, mediaType);
}

/// <summary>
/// Declared request, stream, concurrency, and timeout limits for one endpoint.
/// <see cref="MaxConcurrentCalls"/> and <see cref="Timeout"/> are declared and
/// validated only; the kernel ratifies and enforces backpressure budgets in Step 11D.
/// </summary>
internal sealed record EndpointLimits(
    long MaxRequestBytes,
    long MaxContentBytes,
    int MaxConcurrentCalls,
    TimeSpan Timeout)
{
    /// <summary>Inherit the host's transfer limit.</summary>
    public const long Inherit = -1;

    private const int DefaultConcurrency = 64;

    public static EndpointLimits BodyFree { get; } =
        new(0, 0, DefaultConcurrency, TimeSpan.FromMinutes(2));

    public static EndpointLimits PackageTransfer { get; } =
        new(Inherit, Inherit, DefaultConcurrency, TimeSpan.FromMinutes(30));

    public static EndpointLimits BoundedBody(long maximumBytes) =>
        new(maximumBytes, maximumBytes, DefaultConcurrency, TimeSpan.FromMinutes(2));

    /// <summary>
    /// Resolves declared limits against the host budget. The host passes plain byte
    /// budgets so descriptors never reference a host or storage implementation type.
    /// </summary>
    public EndpointLimits Resolve(long maxRequestBodyBytes, long maxContentBytes) =>
        this with
        {
            MaxRequestBytes = MaxRequestBytes == Inherit
                ? maxRequestBodyBytes
                : Math.Min(MaxRequestBytes, maxRequestBodyBytes),
            MaxContentBytes = MaxContentBytes == Inherit
                ? maxContentBytes
                : Math.Min(MaxContentBytes, maxContentBytes)
        };
}

/// <summary>
/// A declared route or query parameter. Required route parameters must appear in the
/// path template; query parameters are documented for binding completeness.
/// </summary>
internal sealed record EndpointParameter(
    string Name,
    bool IsRequired = true,
    RouteParameterKind Kind = RouteParameterKind.Text);

/// <summary>
/// An operation an endpoint may dispatch, together with the request and response
/// contract versions the descriptor was compiled against.
/// </summary>
internal sealed record EndpointOperationBinding(
    string OperationId,
    string RequestContract,
    string ResponseContract);

/// <summary>
/// A typed, transport-neutral endpoint descriptor. Descriptors are the only way to add
/// a route: the kernel validates them, generates the route table from them, and freezes
/// that table before the host listens. Descriptors never reference
/// <c>WebApplication</c>, endpoint routing, dependency injection, or
/// the HTTP request context.
/// </summary>
internal sealed record EndpointDescriptor
{
    /// <summary>Deterministic, unique route name.</summary>
    public required string Name { get; init; }

    public required ImmutableArray<string> Methods { get; init; }

    public required string PathTemplate { get; init; }

    public required ImmutableArray<EndpointOperationBinding> Operations { get; init; }

    public required IEndpointHandler Handler { get; init; }

    public required EndpointAccessPolicy Access { get; init; }

    public required EndpointLimits Limits { get; init; }

    public ImmutableArray<EndpointParameter> RouteParameters { get; init; } = [];

    public ImmutableArray<EndpointParameter> QueryParameters { get; init; } = [];

    public required EndpointBodyBinding Body { get; init; }

    public EndpointHeadPolicy Head { get; init; } = EndpointHeadPolicy.None;

    /// <summary>
    /// Allows this route's static prefix to be projected as a service resource base.
    /// </summary>
    public bool AllowsResourceBaseReference { get; init; }

    public bool AllowsFragmentReference { get; init; }

    /// <summary>
    /// Routes that exist only when the host runs with a production identity.
    /// </summary>
    public bool RequiresProductionIdentity { get; init; }

    public bool AppliesTo(bool hasProductionIdentity) =>
        !RequiresProductionIdentity || hasProductionIdentity;

    public static EndpointOperationBinding Operation<TRequest, TResponse>(string operationId) =>
        new(operationId, $"{typeof(TRequest).Name}.v1", $"{typeof(TResponse).Name}.v1");
}

/// <summary>
/// Semantic path-template parsing shared by descriptor validation and route generation.
/// </summary>
internal static class EndpointPathTemplate
{
    /// <summary>
    /// Reads declared parameter names without validating the template. Callers that
    /// need validation use <see cref="TryCanonicalize"/>.
    /// </summary>
    public static ImmutableArray<string> ReadParameterNames(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var names = ImmutableArray.CreateBuilder<string>();
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                names.Add(template[(open + 1)..].TrimStart('*'));
                break;
            }

            names.Add(template[(open + 1)..close].TrimStart('*'));
            index = close + 1;
        }

        return names.ToImmutable();
    }

    /// <summary>
    /// Produces the semantic form of a template, where every parameter is replaced by a
    /// placeholder. Two templates with the same canonical form match the same requests.
    /// </summary>
    public static bool TryCanonicalize(string template, out string canonical, out string? error)
    {
        canonical = string.Empty;
        error = null;
        if (string.IsNullOrEmpty(template) || template[0] != '/')
        {
            error = "the template must start with '/'";
            return false;
        }

        if (template.Length > 1 && template.EndsWith('/'))
        {
            error = "the template must not end with '/'";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var segments = template.Split('/');
        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < segments.Length; index++)
        {
            if (segments[index].Length == 0)
            {
                error = "the template contains an empty segment";
                return false;
            }

            if (!TryCanonicalizeSegment(segments[index], names, out var canonicalSegment, out error))
            {
                return false;
            }

            builder.Append('/').Append(canonicalSegment);
        }

        canonical = builder.Length == 0 ? "/" : builder.ToString();
        return true;
    }

    private static bool TryCanonicalizeSegment(
        string segment,
        HashSet<string> names,
        out string canonical,
        out string? error)
    {
        canonical = string.Empty;
        error = null;
        var builder = new System.Text.StringBuilder();
        var index = 0;
        while (index < segment.Length)
        {
            var character = segment[index];
            if (character == '}')
            {
                error = "the template contains an unbalanced '}'";
                return false;
            }

            if (character != '{')
            {
                builder.Append(character);
                index++;
                continue;
            }

            var close = segment.IndexOf('}', index);
            if (close < 0)
            {
                error = "the template contains an unbalanced '{'";
                return false;
            }

            var name = segment[(index + 1)..close];
            if (name.StartsWith('*'))
            {
                error = "catch-all parameters are not allowed";
                return false;
            }

            if (name.Length == 0)
            {
                error = "the template contains an unnamed parameter";
                return false;
            }

            if (name.Contains(':', StringComparison.Ordinal) ||
                name.Contains('?', StringComparison.Ordinal) ||
                name.Contains('=', StringComparison.Ordinal))
            {
                error = $"parameter '{name}' must not declare inline constraints or defaults";
                return false;
            }

            if (!names.Add(name))
            {
                error = $"parameter '{name}' is declared more than once";
                return false;
            }

            builder.Append("{}");
            index = close + 1;
        }

        canonical = builder.ToString();
        return true;
    }
}
