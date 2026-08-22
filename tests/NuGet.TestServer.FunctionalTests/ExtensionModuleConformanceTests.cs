using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.RouteFixture;

namespace NuGet.TestServer.FunctionalTests;

/// <summary>
/// Step 11C conformance proof. A separately compiled module that references only the
/// extension abstractions contributes its identity, one operation, one route, one
/// service-index resource, and one requested capability, and it composes through the
/// same catalog, registry, dispatcher, broker, projection, and startup validation as an
/// official extension.
/// </summary>
public sealed class ExtensionModuleConformanceTests
{
    [Fact]
    public async Task The_module_serves_its_route_through_the_real_gateway()
    {
        await using var server = await FlavorsHost.StartAsync();

        using var index = await server.HttpClient.GetAsync("/flavors/index.json");
        using var filtered = await server.HttpClient.GetAsync("/flavors/index.json?filter=s");
        using var head = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/flavors/index.json"));

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());
        Assert.Equal(
            ["salty", "sweet", "umami"],
            document.RootElement.GetProperty("flavors")
                .EnumerateArray()
                .Select(flavor => flavor.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using var filteredDocument = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert.Equal(
            ["salty", "sweet"],
            filteredDocument.RootElement.GetProperty("flavors")
                .EnumerateArray()
                .Select(flavor => flavor.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_module_reads_the_host_clock_through_the_broker()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await using var server = await FlavorsHost.StartAsync();

        using var response = await server.HttpClient.GetAsync("/flavors/index.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var observedAt = document.RootElement.GetProperty("observedAt").GetDateTimeOffset();
        Assert.InRange(observedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task The_module_renders_through_the_transport_neutral_result_contract()
    {
        await using var server = await FlavorsHost.StartAsync();

        using var response = await server.HttpClient.GetAsync("/flavors/index.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The module returned a document plus a typed route reference; the kernel chose
        // the status code, serialized the body, and projected the absolute URL.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new Uri(server.BaseUrl, "/flavors/index.json").AbsoluteUri,
            document.RootElement.GetProperty("@id").GetString());
    }

    [Fact]
    public async Task The_service_index_advertises_the_module_resource_with_a_projected_url()
    {
        await using var server = await FlavorsHost.StartAsync();

        using var response = await server.HttpClient.GetAsync("/v3/index.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var resource = document.RootElement.GetProperty("resources")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("@type").GetString() ==
                $"{FlavorsModule.ResourceType}/{FlavorsModule.ResourceVersion}");
        var id = resource.GetProperty("@id").GetString();

        Assert.Equal(
            new Uri(server.BaseUrl, "/flavors/index.json").AbsoluteUri,
            id);
        Assert.Equal("Contoso flavor catalog.", resource.GetProperty("comment").GetString());

        // The advertised URL is a real, servable route.
        using var followed = await server.HttpClient.GetAsync(id);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [Fact]
    public async Task Module_routes_and_resources_are_absent_from_hosts_that_do_not_select_it()
    {
        await using var withModule = await FlavorsHost.StartAsync();
        await using var withoutModule = await NuGetTestServerHost.StartAsync();

        using var present = await withModule.HttpClient.GetAsync("/flavors/index.json");
        using var absent = await withoutModule.HttpClient.GetAsync("/flavors/index.json");
        using var index = await withoutModule.HttpClient.GetAsync("/v3/index.json");
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, present.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.DoesNotContain(
            document.RootElement.GetProperty("resources").EnumerateArray(),
            entry => entry.GetProperty("@type").GetString()?.StartsWith(
                FlavorsModule.ResourceType,
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_required_capability_that_is_not_granted_fails_startup()
    {
        var module = new FlavorsModule();
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                module.Contribution.Selection
            ]
        };

        var failure = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [module]));

        Assert.Contains("catalog.missing-capability-grant", failure.Message, StringComparison.Ordinal);
        Assert.Contains(KernelCapabilityNames.HostClockRead, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_granted_capability_is_scoped_to_the_module_that_requested_it()
    {
        var module = new FlavorsModule();
        var composition = FlavorsHost.CreateComposition(module);

        var granted = composition.ExtensionGraph.Capabilities
            .Where(capability => capability.Name == KernelCapabilityNames.HostClockRead)
            .ToArray();

        var scoped = Assert.Single(granted);
        Assert.Equal(FlavorsModule.ExtensionId, scoped.ExtensionId);
        Assert.True(scoped.IsGranted);
        Assert.True(scoped.IsRequired);
        Assert.DoesNotContain(
            composition.ExtensionGraph.Capabilities,
            capability => capability.Name == KernelCapabilityNames.HostClockRead &&
                          capability.ExtensionId != FlavorsModule.ExtensionId);
    }

    [Fact]
    public void A_module_that_claims_an_operation_it_does_not_own_fails_startup()
    {
        var module = new ConflictingModule(
            "contoso.conflicting",
            "/conflicting/index.json",
            "conflicting.index",
            declareOperation: false);
        var profile = FlavorsHost.ProfileWith(module);

        var failure = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [module]));

        Assert.Contains("inactive-endpoint-operation", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_modules_that_claim_the_same_operation_fail_startup()
    {
        var first = new FlavorsModule();
        var second = new ConflictingModule(
            "contoso.duplicate",
            "/duplicate/index.json",
            "contoso.duplicate.index",
            declareOperation: true,
            operationId: FlavorsModule.GetIndexOperationId);
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                first.Contribution.Selection,
                second.Contribution.Selection
            ],
            Grants =
            [
                .. ServerProfiles.Embedded.Grants,
                new CapabilityGrant(KernelCapabilityNames.HostClockRead)
            ]
        };

        var failure = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [first, second]));

        Assert.Contains("operation-owner-conflict", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_module_cannot_be_contributed_twice()
    {
        var module = new FlavorsModule();

        var failure = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                FlavorsHost.ProfileWith(module),
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [module, new FlavorsModule()]));

        Assert.Contains("catalog.duplicate-module", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parallel_hosts_resolve_the_module_independently()
    {
        var first = FlavorsHost.CreateComposition(new FlavorsModule());
        var second = FlavorsHost.CreateComposition(new FlavorsModule());

        Assert.NotSame(first.ExtensionGraph, second.ExtensionGraph);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.Equal(
            first.ExtensionGraph.Routes.Select(route => $"{route.Method} {route.Path}"),
            second.ExtensionGraph.Routes.Select(route => $"{route.Method} {route.Path}"));
        Assert.Contains(
            first.ExtensionGraph.Routes,
            route => route.Path == "/flavors/index.json" &&
                     route.ExtensionId == FlavorsModule.ExtensionId);
    }

    [Fact]
    public async Task Separately_compiled_module_adds_namespaced_registration_metadata()
    {
        var module = new RegistrationLabelsModule();
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                module.Contribution.Selection
            ]
        };
        await using var server = await NuGetTestServerHost.StartCompositionAsync(
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [module]),
            CancellationToken.None);
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Contributed.Package", "1.0.0").Build());

        using var response = await server.HttpClient.GetAsync(
            "/registration/contributed.package/1.0.0.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var extensions = document.RootElement
            .GetProperty("catalogEntry")
            .GetProperty("extensions");
        var contribution = extensions.GetProperty(RegistrationLabelsModule.Namespace);

        Assert.Equal(
            ["approved", "contributed.package"],
            contribution.GetProperty("labels")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            [RegistrationLabelsModule.Namespace],
            extensions.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void Duplicate_registration_contributor_namespaces_fail_composition()
    {
        var first = new RegistrationLabelsModule();
        var second = new RegistrationLabelsModule(
            "contoso.duplicate-registration-labels",
            RegistrationLabelsModule.Namespace);
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                first.Contribution.Selection,
                second.Contribution.Selection
            ]
        };

        var failure = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [first, second]));

        Assert.Contains(
            "document-contributor-namespace-conflict",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 20_000)]
    public async Task Registration_contributor_failure_or_oversize_fails_the_response(
        bool fail,
        int payloadSize)
    {
        var module = new RegistrationLabelsModule(fail: fail, payloadSize: payloadSize);
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                module.Contribution.Selection
            ]
        };
        await using var server = await NuGetTestServerHost.StartCompositionAsync(
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [module]),
            CancellationToken.None);
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Rejected.Contribution", "1.0.0").Build());

        using var response = await server.HttpClient.GetAsync(
            "/registration/rejected.contribution/1.0.0.json");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}

internal static class FlavorsHost
{
    public static ServerProfile ProfileWith(IExtensionModule module) =>
        ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                module.Contribution.Selection
            ],
            Grants =
            [
                .. ServerProfiles.Embedded.Grants,
                new CapabilityGrant(KernelCapabilityNames.HostClockRead)
            ]
        };

    public static ServerComposition CreateComposition(IExtensionModule module) =>
        ServerComposition.Create(
            ProfileWith(module),
            authentication: AuthenticationConfiguration.Anonymous,
            modules: [module]);

    public static Task<NuGetTestServerHost> StartAsync() =>
        NuGetTestServerHost.StartCompositionAsync(
            CreateComposition(new FlavorsModule()),
            CancellationToken.None);
}

/// <summary>
/// A second separately composed module used to prove ownership validation. It is
/// declared in the test assembly so the kernel still has no knowledge of any module.
/// </summary>
internal sealed class ConflictingModule : IExtensionModule
{
    public ConflictingModule(
        string extensionId,
        string path,
        string routeName,
        bool declareOperation,
        string? operationId = null)
    {
        var id = operationId ?? $"{extensionId}.GetIndex";
        var descriptor = new EndpointDescriptor
        {
            Name = routeName,
            Methods = ["GET"],
            PathTemplate = path,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Body = EndpointBodyBinding.None,
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetFlavorIndexRequest, GetFlavorIndexResponse>(id)
            ],
            Handler = EndpointHandler.Create<GetFlavorIndexRequest, GetFlavorIndexResponse>(
                id,
                _ => new GetFlavorIndexRequest(null))
        };
        Contribution = new ExtensionModuleContribution(
            new ExtensionManifest(
                1,
                extensionId,
                new ExtensionVersion(1, 0, 0),
                ExtensionVersionRange.Major(1),
                [],
                declareOperation ? [id] : [],
                [descriptor],
                [],
                []),
            operationId is null
                ?
                [
                    new OperationBinding(
                        new OperationContract(
                            new OperationId(id),
                            OperationFamily.Custom("Contoso.Conflicting"),
                            1,
                            $"{nameof(GetFlavorIndexRequest)}.v1",
                            $"{nameof(GetFlavorIndexResponse)}.v1"),
                        typeof(GetFlavorIndexRequest),
                        typeof(GetFlavorIndexResponse))
                ]
                : ImmutableArray<OperationBinding>.Empty);
    }

    public ExtensionModuleContribution Contribution { get; }

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource documentContributions)
    {
    }
}
