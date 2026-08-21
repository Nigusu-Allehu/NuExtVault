using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Synthetic endpoint descriptors and operation contracts for catalog tests.
/// </summary>
internal static class TestEndpointDescriptors
{
    public const string OperationA = "test.operation.a";
    public const string OperationB = "test.operation.b";
    public const string OperationC = "test.operation.c";

    public static IReadOnlyDictionary<string, OperationBinding> Contracts { get; } =
        ImmutableDictionary<string, OperationBinding>.Empty
            .Add(OperationA, Contract(OperationA))
            .Add(OperationB, Contract(OperationB))
            .Add(OperationC, Contract(OperationC));

    public static EndpointDescriptor Endpoint(
        string name,
        string method,
        string path,
        string operationId = OperationA) =>
        new()
        {
            Name = name,
            Methods = [method],
            PathTemplate = path,
            Operations =
            [
                EndpointDescriptor.Operation<EmptyRequest, EmptyResponse>(operationId)
            ],
            Handler = EndpointHandler.Create((_, _) => ValueTask.FromResult(
                EndpointInvocation.Result(OperationResult.NoContent()))),
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Body = EndpointBodyBinding.None,
            Limits = EndpointLimits.BodyFree,
            RouteParameters =
            [
                .. EndpointPathTemplate.ReadParameterNames(path)
                    .Select(parameter => new EndpointParameter(parameter))
            ]
        };

    public static OperationBinding Contract(string operationId) =>
        new(
            new OperationContract(
                new OperationId(operationId),
                OperationFamily.Diagnostics,
                1,
                $"{nameof(EmptyRequest)}.v1",
                $"{nameof(EmptyResponse)}.v1"),
            typeof(EmptyRequest),
            typeof(EmptyResponse));

    public static ResolvedExtensionGraph ResolveWithTestContracts(
        this ExtensionCatalog catalog,
        ServerProfile profile,
        bool hasProductionIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Resolve(profile, hasProductionIdentity, Contracts);
    }

    /// <summary>
    /// Resolves a synthetic catalog with an <c>EmptyRequest</c>/<c>EmptyResponse</c>
    /// contract for every operation id it declares, so composition tests do not depend
    /// on the built-in contract set.
    /// </summary>
    public static ResolvedExtensionGraph ResolveWith(
        this ExtensionCatalog catalog,
        ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Resolve(profile, contracts: SyntheticContracts);
    }

    private static IReadOnlyDictionary<string, OperationBinding> SyntheticContracts { get; } =
        new SyntheticContractIndex();

    /// <summary>
    /// Supplies a synthetic contract for any operation id a composition test declares.
    /// </summary>
    private sealed class SyntheticContractIndex : IReadOnlyDictionary<string, OperationBinding>
    {
        public OperationBinding this[string key] => Contract(key);

        public IEnumerable<string> Keys => [];

        public IEnumerable<OperationBinding> Values => [];

        public int Count => 0;

        public bool ContainsKey(string key) => true;

        public bool TryGetValue(string key, out OperationBinding value)
        {
            value = Contract(key);
            return true;
        }

        public IEnumerator<KeyValuePair<string, OperationBinding>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, OperationBinding>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
