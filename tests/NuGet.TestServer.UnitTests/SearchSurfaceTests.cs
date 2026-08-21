using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Hosting.Endpoints;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Owners.Search;

namespace NuGet.TestServer.UnitTests;

public sealed class SearchSurfaceTests
{
    [Fact]
    public void Search_surface_is_complete_and_body_free()
    {
        var endpoint = Assert.Single(SearchEndpoints.All);

        Assert.Equal("search.query", endpoint.Name);
        Assert.Equal(["GET", "HEAD"], endpoint.Methods.ToArray());
        Assert.Equal(EndpointHeadPolicy.MirrorsGet, endpoint.Head);
        Assert.Equal(EndpointBodyBinding.None, endpoint.Body);
        Assert.Equal(EndpointLimits.BodyFree, endpoint.Limits);
        Assert.Equal(
            OperationIds.SearchQuery,
            Assert.Single(endpoint.Operations).OperationId);
    }

    [Fact]
    public void Search_owner_depends_only_on_its_query_adapter()
    {
        var constructor = Assert.Single(typeof(SearchOperations).GetConstructors());

        Assert.Equal(
            [typeof(ISearchPackageQuery)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
