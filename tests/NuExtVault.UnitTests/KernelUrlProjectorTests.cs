using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Hosting;
using NuExtVault.Kernel.Routing;
using NuExtVault.Packages;

namespace NuExtVault.UnitTests;

public sealed class KernelUrlProjectorTests
{
    [Fact]
    public void Projects_normalized_route_parameters_against_the_frozen_table()
    {
        var projector = CreateProjector();
        var reference = RouteReference.Endpoint(
            "registration.leaf",
            RouteParameterValue.PackageId("id", "Example.Package"),
            RouteParameterValue.PackageVersion("version", "2.0"));

        var projected = projector.Project(
            reference,
            new PublicUrlOrigin("https", "packages.example.test", "/nuget"));

        Assert.Equal(
            "https://packages.example.test/nuget/registration/example.package/2.0.0.json",
            projected);
    }

    [Fact]
    public void Escapes_text_parameters_without_accepting_path_injection()
    {
        var projector = CreateProjector();
        var reference = RouteReference.Endpoint(
            "vulnerabilities.page",
            RouteParameterValue.Text("snapshotId", "snapshot value"),
            RouteParameterValue.Text("pageName", "base value"));

        Assert.Equal(
            "https://packages.example.test/v3/vulnerabilities/snapshot%20value/base%20value.json",
            projector.Project(
                reference,
                new PublicUrlOrigin("https", "packages.example.test", string.Empty)));
        Assert.Throws<ArgumentException>(
            () => RouteParameterValue.Text("pageName", "base/value"));
    }

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public void Rejects_invalid_route_references(object referenceValue, string errorCode)
    {
        var reference = Assert.IsType<RouteReference>(referenceValue);
        var exception = Assert.Throws<RouteProjectionException>(() =>
            CreateProjector().Project(
                reference,
                new PublicUrlOrigin("https", "packages.example.test", string.Empty)));

        Assert.StartsWith(errorCode, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<object, string> InvalidReferences => new()
    {
        {
            RouteReference.Endpoint("missing.route"),
            "route-reference.unknown-route:"
        },
        {
            RouteReference.Endpoint("registration.leaf"),
            "route-reference.missing-parameter:"
        },
        {
            RouteReference.Endpoint(
                "registration.leaf",
                RouteParameterValue.PackageId("id", "Example.Package"),
                RouteParameterValue.PackageVersion("version", "1.0.0"),
                RouteParameterValue.Text("extra", "value")),
            "route-reference.extra-parameter:"
        },
        {
            RouteReference.Endpoint(
                "registration.leaf",
                RouteParameterValue.Text("id", "Example.Package"),
                RouteParameterValue.PackageVersion("version", "1.0.0")),
            "route-reference.parameter-type:"
        },
        {
            new RouteReference(
                "registration.leaf",
                RouteReferenceTarget.Endpoint,
                [
                    RouteParameterValue.PackageId("id", "Example.Package"),
                    RouteParameterValue.PackageVersion("version", "1.0.0")
                ],
                [new RouteQueryValue("q", "value")],
                fragment: null),
            "route-reference.extra-query:"
        }
    };

    [Fact]
    public void Service_resource_bases_are_explicitly_permitted_by_the_route_contract()
    {
        var projector = CreateProjector();

        Assert.Equal(
            "https://packages.example.test/flatcontainer/",
            projector.Project(
                RouteReference.Base("flatcontainer.versions"),
                new PublicUrlOrigin("https", "packages.example.test", string.Empty)));
        Assert.Throws<RouteProjectionException>(() => projector.Project(
            RouteReference.Base("registration.leaf"),
            new PublicUrlOrigin("https", "packages.example.test", string.Empty)));
    }

    [Fact]
    public void Query_and_fragment_projection_follow_the_route_contract()
    {
        var projector = CreateProjector();
        var reference = new RouteReference(
            "search.query",
            RouteReferenceTarget.Endpoint,
            [],
            [new RouteQueryValue("q", "a/b c")],
            fragment: null);

        Assert.Equal(
            "https://packages.example.test/query?q=a%2Fb%20c",
            projector.Project(
                reference,
                new PublicUrlOrigin("https", "packages.example.test", string.Empty)));
        var withFragment = new RouteReference(
            reference.RouteName,
            reference.Target,
            reference.Parameters,
            reference.Query,
            "section");
        var exception = Assert.Throws<RouteProjectionException>(() => projector.Project(
            withFragment,
            new PublicUrlOrigin("https", "packages.example.test", string.Empty)));
        Assert.StartsWith("route-reference.fragment-not-permitted:", exception.Message);
    }

    [Fact]
    public void Public_origin_uses_trusted_forwarded_values_and_path_base()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 8080);
        context.Request.PathBase = "/internal";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "packages.example.test";
        context.Request.Headers["X-Forwarded-Prefix"] = "/nuget";

        var origin = PublicUrlOrigin.FromRequest(
            context,
            new TransportSecurityOptions(new TrustedProxyOptions(["127.0.0.1"])));

        Assert.Equal(new PublicUrlOrigin("https", "packages.example.test", "/nuget"), origin);
    }

    [Fact]
    public void Public_origin_ignores_forwarded_values_from_untrusted_clients()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 8080);
        context.Request.PathBase = "/direct";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "packages.example.test";
        context.Request.Headers["X-Forwarded-Prefix"] = "/nuget";

        var origin = PublicUrlOrigin.FromRequest(
            context,
            new TransportSecurityOptions(new TrustedProxyOptions(["127.0.0.1"])));

        Assert.Equal(new PublicUrlOrigin("http", "127.0.0.1:8080", "/direct"), origin);
    }

    [Fact]
    public void Public_origin_supports_ipv6_authorities()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("::1", 8080);
        context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;

        var origin = PublicUrlOrigin.FromRequest(context, new TransportSecurityOptions(null));

        Assert.Equal("http://[::1]:8080/v3/index.json", CreateProjector().Project(
            RouteReference.Endpoint("service-index.get"),
            origin));
    }

    [Theory]
    [InlineData("https", "example.test:443", "example.test")]
    [InlineData("http", "[0:0:0:0:0:0:0:1]:8080", "[::1]:8080")]
    public void Public_origin_canonicalizes_valid_authorities(
        string scheme,
        string authority,
        string expectedAuthority)
    {
        var origin = new PublicUrlOrigin(scheme, authority, string.Empty);

        Assert.Equal(expectedAuthority, origin.Authority);
    }

    [Fact]
    public void Public_origin_rejects_ambiguous_forwarded_values()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 8080);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-Host"] = "a.example.test,b.example.test";

        var exception = Assert.Throws<RouteProjectionException>(() =>
            PublicUrlOrigin.FromRequest(
                context,
                new TransportSecurityOptions(new TrustedProxyOptions(["127.0.0.1"]))));

        Assert.StartsWith("route-reference.invalid-forwarded-header:", exception.Message);
    }

    [Theory]
    [InlineData("victim.example@evil.example")]
    [InlineData("evil.example/path")]
    [InlineData("evil.example?query")]
    [InlineData("evil.example#fragment")]
    public void Public_origin_rejects_non_authority_forwarded_hosts(string forwardedHost)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 8080);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-Host"] = forwardedHost;

        var exception = Assert.Throws<RouteProjectionException>(() =>
            PublicUrlOrigin.FromRequest(
                context,
                new TransportSecurityOptions(new TrustedProxyOptions(["127.0.0.1"]))));

        Assert.StartsWith("route-reference.invalid-origin:", exception.Message);
    }

    private static KernelUrlProjector CreateProjector()
    {
        var graph = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Embedded);
        var routes = KernelRouteTable.Create(graph, PackageTransferLimits.Default, false);
        return new KernelUrlProjector(routes);
    }
}
