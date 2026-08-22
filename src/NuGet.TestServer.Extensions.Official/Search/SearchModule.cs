using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Search;

/// <summary>
/// The official <c>NuGet.Search</c> extension. It owns indexed search, the query route,
/// and the advertised search resources while authoritative visibility remains in the
/// kernel capability.
/// </summary>
internal sealed class SearchModule : IExtensionModule
{
    public const string ExtensionId = "builtin.search";

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [OperationIds.SearchQuery],
            SearchEndpoints.Descriptors,
            [
                Resource("3.0.0-beta", 30),
                Resource("3.5.0", 40)
            ],
            [
                new CapabilityRequest(KernelCapabilityNames.PackageSearchQuery, true)
            ]),
        []);

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource documentContributions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(documentContributions);
        new SearchOperations(
            capabilities.GetRequired<ISearchIndexQueryCapability>(
                KernelCapabilityNames.PackageSearchQuery)).Register(registry);
    }

    private static ServiceResourceContribution Resource(string version, int order) =>
        new(
            "SearchQueryService",
            version,
            new OperationId(OperationIds.SearchQuery),
            "/query",
            ServiceResourceVisibility.Advertised,
            ServiceResourceAccess.Read,
            [],
            ["PackageBaseAddress/3.0.0", "RegistrationsBaseUrl/3.6.0"],
            null,
            order,
            ServiceResourceReadiness.Ready);
}
