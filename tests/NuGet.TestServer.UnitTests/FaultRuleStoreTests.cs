using System.Net;
using NuGet.TestServer.Hosting;
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

    [Fact]
    public void Store_rejects_rules_beyond_its_configured_capacity()
    {
        var store = new FaultRuleStore(new RuntimeStateConfiguration(
            requestHistoryCapacity: RuntimeStateConfiguration.DefaultRequestHistoryCapacity,
            faultRuleCapacity: 2));
        store.Add(CreateRule("first"));
        store.Add(CreateRule("second"));

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => store.Add(CreateRule("third")));

        Assert.Contains("capacity of 2", exception.Message);
        Assert.Equal(["first", "second"], store.GetAll().Select(rule => rule.Id));
    }

    [Fact]
    public void Store_never_exceeds_capacity_under_concurrent_adds()
    {
        const int capacity = 32;
        var store = new FaultRuleStore(new RuntimeStateConfiguration(
            requestHistoryCapacity: RuntimeStateConfiguration.DefaultRequestHistoryCapacity,
            faultRuleCapacity: capacity));

        Parallel.For(0, 1_000, index =>
        {
            try
            {
                store.Add(CreateRule($"rule-{index:D4}"));
            }
            catch (InvalidOperationException)
            {
            }
        });

        Assert.Equal(capacity, store.GetAll().Count);
    }

    [Fact]
    public void Reset_releases_fault_rule_capacity()
    {
        var store = new FaultRuleStore(new RuntimeStateConfiguration(
            requestHistoryCapacity: RuntimeStateConfiguration.DefaultRequestHistoryCapacity,
            faultRuleCapacity: 1));
        store.Add(CreateRule("first"));

        store.Reset();
        store.Add(CreateRule("second"));

        Assert.Equal(["second"], store.GetAll().Select(rule => rule.Id));
    }

    private static FaultRule CreateRule(string id) =>
        new(
            Id: id,
            Method: "GET",
            PathContains: "/",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 1,
            Delay: TimeSpan.Zero);
}
