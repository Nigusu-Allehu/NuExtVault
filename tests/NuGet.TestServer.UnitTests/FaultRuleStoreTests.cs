using System.Net;
using NuGet.TestServer.Faults;

namespace NuGet.TestServer.UnitTests;

public sealed class FaultRuleStoreTests
{
    [Fact]
    public void Rule_matches_case_insensitively_and_is_consumed_exactly()
    {
        var store = new FaultRuleStore();
        store.Add(new FaultRule(
            Id: "download-failure",
            Method: "GET",
            PathContains: "/flatcontainer/example/",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 2,
            Delay: TimeSpan.Zero));

        Assert.NotNull(store.Match("get", "/FLATCONTAINER/EXAMPLE/1.0.0/example.1.0.0.nupkg"));
        Assert.NotNull(store.Match("GET", "/flatcontainer/example/index.json"));
        Assert.Null(store.Match("GET", "/flatcontainer/example/index.json"));
    }
}
