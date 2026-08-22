using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.Search;

internal sealed class SearchOperations(ISearchIndexQueryCapability packages)
{
    public void Register(IOperationOwnerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            SearchModule.ExtensionId,
            OperationOwner.Create<SearchRequest, SearchResponse>(
                OperationIds.SearchQuery,
                SearchAsync));
    }

    private async ValueTask<OperationResponse<SearchResponse>> SearchAsync(
        SearchRequest request,
        CancellationToken token)
    {
        var page = await packages.QueryAsync(
            new IndexedPackageSearchRequest(
                request.Query,
                request.IncludePrerelease,
                request.Skip,
                request.Take,
                request.PackageType),
            token);
        var response = new SearchResponse(
            page.TotalHits,
            [.. page.Items.Select(SearchDocumentBuilder.CreateResult)]);
        return OperationResponse<SearchResponse>.Success(
            response,
            OperationResult.Ok(new
            {
                totalHits = response.TotalHits,
                data = response.Data.Select(SearchDocumentRenderer.Render).ToArray()
            }));
    }
}
