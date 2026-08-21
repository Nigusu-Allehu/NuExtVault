using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Kernel.Owners.Search;

internal sealed class SearchOperations
{
    private readonly ISearchPackageQuery _packages;

    public SearchOperations(ISearchPackageQuery packages)
    {
        _packages = packages;
    }

    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.Protocol,
            new DelegateOperationOwner<SearchRequest, SearchResponse>(
                OperationIds.SearchQuery,
                SearchAsync));
    }

    private async ValueTask<OperationResponse<SearchResponse>> SearchAsync(
        SearchRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var page = await _packages.SearchAsync(
            request.Query,
            request.IncludePrerelease,
            request.Skip,
            request.Take,
            request.PackageType,
            token);
        var response = new SearchResponse(
            page.TotalHits,
            [
                .. page.Items.Select(item => SearchDocumentBuilder.CreateResult(
                    item.Package,
                    item.Versions))
            ]);
        context.Complete(new OperationResult(
            OperationResultStatus.Ok,
            new OperationDocumentBody(new
            {
                totalHits = response.TotalHits,
                data = response.Data.Select(SearchDocumentRenderer.Render).ToArray()
            })));
        return OperationResponse<SearchResponse>.Success(response);
    }
}
