using System.Collections.Immutable;
using System.Text;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Kernel;

internal sealed record OperationRegistration(
    OperationId Id,
    string ExtensionId,
    Type RequestType,
    Type ResponseType,
    Func<object, OperationExecutionContext, CancellationToken, ValueTask<object>> Invoke);

/// <summary>
/// Per-host-instance registry of typed operation owners. Ownership is validated
/// against the resolved extension graph before the host starts listening, and the
/// resulting order never depends on registration order.
/// </summary>
internal sealed class OperationRegistry
{
    private readonly ImmutableDictionary<string, OperationRegistration> _byId;

    internal OperationRegistry(ImmutableArray<OperationRegistration> registrations, string diagnostics)
    {
        Registrations = registrations;
        Diagnostics = diagnostics;
        _byId = registrations.ToImmutableDictionary(
            registration => registration.Id.Value,
            StringComparer.Ordinal);
    }

    public ImmutableArray<OperationRegistration> Registrations { get; }

    public string Diagnostics { get; }

    public OperationRegistration? Find(string operationId) =>
        _byId.GetValueOrDefault(operationId);

    public bool TryGet(OperationId id, out OperationRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.TryGetValue(id.Value, out registration);
    }
}

internal sealed class OperationRegistryBuilder : IOperationOwnerRegistry
{
    private readonly List<PendingRegistration> _registrations = [];

    public OperationRegistryBuilder Register<TRequest, TResponse>(
        string extensionId,
        IOperationOwner<TRequest, TResponse> owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(owner);
        _registrations.Add(new PendingRegistration(
            owner.OperationId,
            extensionId,
            typeof(TRequest),
            typeof(TResponse),
            (request, context, token) => InvokeAsync(owner, request, context, token)));
        return this;
    }

    IOperationOwnerRegistry IOperationOwnerRegistry.Register<TRequest, TResponse>(
        string extensionId,
        IOperationOwner<TRequest, TResponse> owner) => Register(extensionId, owner);

    public OperationRegistry Build(
        ResolvedExtensionGraph graph,
        IReadOnlyDictionary<string, OperationBinding>? contracts = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var declaredOwners = graph.Operations.ToDictionary(
            operation => operation.OperationId,
            operation => operation.ExtensionId,
            StringComparer.Ordinal);
        var bindings = contracts ?? OperationContracts.Bindings.ToDictionary(
            binding => binding.Contract.Id.Value,
            StringComparer.Ordinal);
        var registrations = new Dictionary<string, OperationRegistration>(StringComparer.Ordinal);

        foreach (var pending in _registrations
                     .OrderBy(registration => registration.Id.Value, StringComparer.Ordinal)
                     .ThenBy(registration => registration.ExtensionId, StringComparer.Ordinal))
        {
            var id = pending.Id.Value;
            if (!bindings.TryGetValue(id, out var binding))
            {
                throw Failure(
                    "unknown-operation",
                    $"Extension '{pending.ExtensionId}' registered unknown operation '{id}'.");
            }

            if (binding.RequestType != pending.RequestType ||
                binding.ResponseType != pending.ResponseType)
            {
                throw Failure(
                    "contract-mismatch",
                    $"Operation '{id}' declares contracts " +
                    $"'{binding.RequestType.Name}'/'{binding.ResponseType.Name}', but " +
                    $"'{pending.ExtensionId}' registered " +
                    $"'{pending.RequestType.Name}'/'{pending.ResponseType.Name}'.");
            }

            if (registrations.TryGetValue(id, out var existing))
            {
                throw Failure(
                    "duplicate-owner",
                    $"Operation '{id}' is owned by '{existing.ExtensionId}' and " +
                    $"'{pending.ExtensionId}'.");
            }

            if (!declaredOwners.TryGetValue(id, out var declaredOwner))
            {
                throw Failure(
                    "inactive-operation",
                    $"Extension '{pending.ExtensionId}' registered operation '{id}', which the " +
                    "resolved extension graph does not activate.");
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(declaredOwner, pending.ExtensionId))
            {
                throw Failure(
                    "owner-mismatch",
                    $"Operation '{id}' is declared by '{declaredOwner}', but " +
                    $"'{pending.ExtensionId}' registered the handler.");
            }

            registrations.Add(
                id,
                new OperationRegistration(
                    pending.Id,
                    declaredOwner,
                    pending.RequestType,
                    pending.ResponseType,
                    pending.Invoke));
        }

        foreach (var operation in graph.Operations)
        {
            if (!registrations.ContainsKey(operation.OperationId))
            {
                throw Failure(
                    "missing-owner",
                    $"Operation '{operation.OperationId}' declared by " +
                    $"'{operation.ExtensionId}' has no registered handler.");
            }
        }

        var ordered = registrations.Values
            .OrderBy(registration => registration.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return new OperationRegistry(ordered, CreateDiagnostics(graph, ordered));
    }

    private static async ValueTask<object> InvokeAsync<TRequest, TResponse>(
        IOperationOwner<TRequest, TResponse> owner,
        object request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var typed = (TRequest)request;
        var response = owner is IContextualOperationOwner<TRequest, TResponse> contextual
            ? await contextual.HandleAsync(typed, context, token)
            : await owner.HandleAsync(typed, token);
        return response;
    }

    private static string CreateDiagnostics(
        ResolvedExtensionGraph graph,
        ImmutableArray<OperationRegistration> registrations)
    {
        var builder = new StringBuilder();
        builder.Append("profile=").Append(graph.ProfileName).Append('\n');
        foreach (var registration in registrations)
        {
            builder.Append("operation=").Append(registration.Id.Value)
                .Append(" owner=").Append(registration.ExtensionId)
                .Append(" request=").Append(registration.RequestType.Name)
                .Append(" response=").Append(registration.ResponseType.Name)
                .Append('\n');
        }

        return builder.ToString();
    }

    private static ServerHostingConfigurationException Failure(string code, string message) =>
        new($"registry.{code}: {message}");

    private sealed record PendingRegistration(
        OperationId Id,
        string ExtensionId,
        Type RequestType,
        Type ResponseType,
        Func<object, OperationExecutionContext, CancellationToken, ValueTask<object>> Invoke);
}
