using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Search;

internal static class SearchEndpoints
{
    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
    [
        new()
        {
            Name = "search.query",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/query",
            QueryParameters =
            [
                new EndpointParameter("q", IsRequired: false),
                new EndpointParameter("skip", IsRequired: false),
                new EndpointParameter("take", IsRequired: false),
                new EndpointParameter("prerelease", IsRequired: false),
                new EndpointParameter("packageType", IsRequired: false)
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<SearchRequest, SearchResponse>(
                    OperationIds.SearchQuery)
            ],
            Handler = EndpointHandler.Create<SearchRequest, SearchResponse>(
                OperationIds.SearchQuery,
                request => new SearchRequest(
                    request.GetQuery("q") ?? string.Empty,
                    request.GetQueryInt32("skip") ?? 0,
                    request.GetQueryInt32("take") ?? 20,
                    request.GetQueryBoolean("prerelease") ?? false,
                    request.GetQuery("packageType")))
        }
    ];
}
