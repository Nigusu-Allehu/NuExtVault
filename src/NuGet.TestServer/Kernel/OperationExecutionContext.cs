using System.Collections.Concurrent;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// Kernel-owned, per-invocation state for one dispatched operation. Operation owners
/// receive typed contracts only; anything that cannot be expressed as a serializable
/// contract (content streams, caller identity, and the protocol-compatible rendering
/// of the current wire format) travels through this context.
/// </summary>
internal sealed class OperationExecutionContext
{
    private static long _sequence;

    public OperationExecutionContext(
        string hostInstanceId,
        IOperationAuthorization? authorization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        HostInstanceId = hostInstanceId;
        ExecutionId = $"{hostInstanceId}:{Interlocked.Increment(ref _sequence)}";
        Authorization = authorization ?? UnrestrictedOperationAuthorization.Instance;
        Content = new OperationContentStore(ExecutionId);
    }

    public string HostInstanceId { get; }

    public string ExecutionId { get; }

    /// <summary>
    /// The request path of the current call, when the operation was dispatched from
    /// the HTTP gateway. Owners use it only to preserve existing location headers.
    /// </summary>
    public string? RequestPath { get; set; }

    public OperationContentStore Content { get; }

    public IOperationAuthorization Authorization { get; }

    /// <summary>
    /// The protocol-compatible response an owner rendered for the current request.
    /// Kernel error policy is used when an owner does not attach one.
    /// </summary>
    public OperationResult? Result { get; private set; }

    public static OperationExecutionContext CreateDetached() => new("detached");

    public void Complete(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
    }
}

/// <summary>
/// The kernel-internal ambient execution for the operation currently being dispatched.
/// Capability implementations use it to resolve kernel-issued content handles, so an
/// extension never needs the execution context to move bounded content.
/// </summary>
internal static class OperationExecutionScope
{
    private static readonly AsyncLocal<OperationExecutionContext?> CurrentExecution = new();

    public static OperationExecutionContext? Current => CurrentExecution.Value;

    public static OperationExecutionContext Required =>
        CurrentExecution.Value ?? throw new InvalidOperationException(
            "No kernel operation execution is active on this call.");

    public static IDisposable Enter(OperationExecutionContext execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var previous = CurrentExecution.Value;
        CurrentExecution.Value = execution;
        return new Scope(previous);
    }

    private sealed class Scope(OperationExecutionContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentExecution.Value = previous;
            }
        }
    }
}

/// <summary>
/// Kernel-issued content handles. Handles are scoped to one execution so package and
/// symbol content is never copied into a contract or shared across host instances.
/// </summary>
internal sealed class OperationContentStore(string executionId)
{
    private readonly ConcurrentDictionary<string, OperationContent> _content =
        new(StringComparer.Ordinal);
    private long _sequence;

    public StreamHandle RegisterStream(
        Stream stream,
        string contentType,
        long length,
        bool supportsRanges = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Add(new OperationContent(
            CreateHandle(contentType, length),
            contentType,
            length,
            supportsRanges,
            stream,
            null,
            null));
    }

    public StreamHandle RegisterBytes(ReadOnlyMemory<byte> content, string contentType) =>
        Add(new OperationContent(
            CreateHandle(contentType, content.Length),
            contentType,
            content.Length,
            false,
            null,
            content,
            null));

    public StreamHandle RegisterFile(string path, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Add(new OperationContent(
            CreateHandle(contentType, long.MaxValue),
            contentType,
            0,
            false,
            null,
            null,
            path));
    }

    public OperationContent Resolve(StreamHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return TryResolve(handle, out var content)
            ? content!
            : throw new InvalidOperationException(
                $"Content handle '{handle.Id}' does not belong to execution '{executionId}'.");
    }

    public bool TryResolve(StreamHandle handle, out OperationContent? content)
    {
        ArgumentNullException.ThrowIfNull(handle);
        content = null;
        return handle.Id.StartsWith(executionId + "/", StringComparison.Ordinal) &&
               _content.TryGetValue(handle.Id, out content);
    }

    private StreamHandle CreateHandle(string contentType, long maximumLength) =>
        new(
            $"{executionId}/{Interlocked.Increment(ref _sequence)}",
            maximumLength,
            contentType);

    private StreamHandle Add(OperationContent content)
    {
        _content[content.Handle.Id] = content;
        return content.Handle;
    }
}

internal sealed record OperationContent(
    StreamHandle Handle,
    string ContentType,
    long Length,
    bool SupportsRanges,
    Stream? Stream,
    ReadOnlyMemory<byte>? Bytes,
    string? FilePath);

/// <summary>
/// Kernel-owned caller facts. Owners never see the HTTP request, the security
/// configuration, or the audit sink.
/// </summary>
internal interface IOperationAuthorization
{
    bool HasIdentity { get; }

    string? IdentityName { get; }

    bool IsAdministrator { get; }

    bool AllowsPackage(string packageId);

    void RecordDenial(string detail);
}

internal sealed class UnrestrictedOperationAuthorization : IOperationAuthorization
{
    public static UnrestrictedOperationAuthorization Instance { get; } = new();

    public bool HasIdentity => false;

    public string? IdentityName => null;

    public bool IsAdministrator => false;

    public bool AllowsPackage(string packageId) => true;

    public void RecordDenial(string detail)
    {
    }
}

internal sealed class HttpOperationAuthorization(
    HttpContext context,
    ISecurityAuditSink audits) : IOperationAuthorization
{
    private readonly ProductionIdentity? _identity =
        context.Items[typeof(ProductionIdentity)] as ProductionIdentity;

    public bool HasIdentity => _identity is not null;

    public string? IdentityName => _identity?.Name ?? context.User.Identity?.Name;

    public bool IsAdministrator => _identity?.HasScope(SecurityScope.Admin) ?? false;

    public bool AllowsPackage(string packageId) =>
        _identity is null || _identity.AllowsPackage(packageId);

    public void RecordDenial(string detail)
    {
        if (_identity is null)
        {
            return;
        }

        audits.Write(new SecurityAuditEvent(
            DateTimeOffset.UtcNow,
            SecurityAuditEventType.AuthorizationDenied,
            context.Items[SecurityContextItems.Client] as string ??
            context.Connection.RemoteIpAddress?.ToString() ??
            "unknown",
            _identity.Name,
            context.Request.Method,
            context.Request.Path,
            detail));
    }
}
