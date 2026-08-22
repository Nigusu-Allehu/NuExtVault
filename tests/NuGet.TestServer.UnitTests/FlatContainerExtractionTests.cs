using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 13 extraction gates. The five flat-container and symbol read operations, their
/// typed routes, and the package-base-address resource have exactly one owner: the
/// official <c>NuGet.FlatContainer</c> extension.
/// </summary>
public sealed class FlatContainerExtractionTests
{
    internal const string FlatContainerExtensionId = "builtin.flat-container";
    private const string ProtocolExtensionId = "builtin.protocol";
    private const string SymbolsCapability = "packages.symbols.read";

    private static readonly string[] FlatContainerOperations =
    [
        "NuGet.FlatContainer.GetHash",
        "NuGet.FlatContainer.GetNuspec",
        "NuGet.FlatContainer.GetPackage",
        "NuGet.FlatContainer.GetSymbol",
        "NuGet.FlatContainer.GetVersions"
    ];

    [Fact]
    public void Flat_container_operations_have_exactly_one_official_extension_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        foreach (var operationId in FlatContainerOperations)
        {
            var declared = Assert.Single(
                host.Graph.Operations,
                operation => operation.OperationId == operationId);
            var registration = host.Registry.Find(operationId);

            Assert.Equal(FlatContainerExtensionId, declared.ExtensionId);
            Assert.NotNull(registration);
            Assert.Equal(FlatContainerExtensionId, registration!.ExtensionId);
        }

    }

    [Fact]
    public void Flat_container_routes_and_resources_move_with_the_extension()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        var routes = host.Graph.Routes
            .Where(route => route.Path.StartsWith("/flatcontainer/", StringComparison.Ordinal))
            .ToArray();
        var resource = Assert.Single(
            host.Graph.Resources,
            item => item.Contribution.AdvertisedType == "PackageBaseAddress/3.0.0");

        Assert.Equal(4, routes.Length);
        Assert.All(routes, route => Assert.Equal(FlatContainerExtensionId, route.ExtensionId));
        Assert.Equal(
            [
                "GET /flatcontainer/{id}/index.json",
                "GET /flatcontainer/{id}/{version}/{fileName}",
                "HEAD /flatcontainer/{id}/index.json",
                "HEAD /flatcontainer/{id}/{version}/{fileName}"
            ],
            routes.Select(route => $"{route.Method} {route.Path}").Order(StringComparer.Ordinal));
        Assert.Equal(FlatContainerExtensionId, resource.ExtensionId);
        Assert.Equal("/flatcontainer/", resource.Contribution.RouteName);
        Assert.Equal(10, resource.Contribution.Order);
    }

    [Fact]
    public void Flat_container_endpoint_descriptors_are_preserved_verbatim()
    {
        var manifest = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == FlatContainerExtensionId);

        Assert.Equal(
            ["flatcontainer.content", "flatcontainer.versions"],
            manifest.Endpoints.Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            FlatContainerOperations,
            manifest.OwnedOperations.Order(StringComparer.Ordinal));

        var versions = Assert.Single(
            manifest.Endpoints,
            endpoint => endpoint.Name == "flatcontainer.versions");
        Assert.Equal("/flatcontainer/{id}/index.json", versions.PathTemplate);
        Assert.Equal(["GET", "HEAD"], versions.Methods.ToArray());
        Assert.Equal(EndpointHeadPolicy.MirrorsGet, versions.Head);
        Assert.Equal(EndpointAccessKind.Read, versions.Access.Default);
        Assert.Equal(EndpointLimits.BodyFree, versions.Limits);
        Assert.True(versions.AllowsResourceBaseReference);
        Assert.Equal(
            [("id", RouteParameterKind.PackageId)],
            versions.RouteParameters.Select(parameter => (parameter.Name, parameter.Kind)));

        var content = Assert.Single(
            manifest.Endpoints,
            endpoint => endpoint.Name == "flatcontainer.content");
        Assert.Equal("/flatcontainer/{id}/{version}/{fileName}", content.PathTemplate);
        Assert.Equal(["GET", "HEAD"], content.Methods.ToArray());
        Assert.Equal(EndpointHeadPolicy.MirrorsGet, content.Head);
        Assert.Equal(EndpointBodyKind.None, content.Body.Kind);
        Assert.Equal(
            [
                "NuGet.FlatContainer.GetHash",
                "NuGet.FlatContainer.GetNuspec",
                "NuGet.FlatContainer.GetPackage"
            ],
            content.Operations
                .Select(operation => operation.OperationId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                ("fileName", RouteParameterKind.Text),
                ("id", RouteParameterKind.PackageId),
                ("version", RouteParameterKind.PackageVersion)
            ],
            content.RouteParameters
                .Select(parameter => (parameter.Name, parameter.Kind))
                .OrderBy(parameter => parameter.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void Flat_container_requests_narrow_package_capabilities_and_protocol_gives_up_content()
    {
        var flatContainer = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == FlatContainerExtensionId);
        var protocol = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == ProtocolExtensionId);

        Assert.Equal(
            [
                "packages.content.read",
                "packages.identity.read",
                "packages.metadata.read",
                SymbolsCapability
            ],
            flatContainer.RequestedCapabilities
                .Select(capability => capability.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(
            flatContainer.RequestedCapabilities,
            capability => Assert.True(capability.IsRequired));
        Assert.DoesNotContain(
            protocol.RequestedCapabilities,
            capability => capability.Name is "packages.content.read" or SymbolsCapability);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    [InlineData("production")]
    public void Every_profile_selects_the_flat_container_extension_and_grants_its_capabilities(
        string profileName)
    {
        var profile = profileName switch
        {
            "embedded" => ServerProfiles.Embedded,
            "standard" => ServerProfiles.Standard,
            _ => ServerProfiles.Production
        };

        Assert.Contains(
            profile.Extensions,
            extension => extension.Id == FlatContainerExtensionId);
        Assert.Contains(profile.Grants, grant => grant.Name == SymbolsCapability);
    }

    [Fact]
    public void Kernel_composition_never_names_the_flat_container_extension()
    {
        // The official module list is the single, generic registration point. No kernel
        // composition file (catalog, profiles, registry, capability requirements, routing)
        // may branch on the flat-container identity.
        var extensionRoot = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "FlatContainer");
        var moduleList = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "OfficialExtensionModules.cs");
        var registrationRoot = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "Registration");
        var pattern = new Regex(
            Regex.Escape(FlatContainerExtensionId) + "|FlatContainerModule|FlatContainerOperations",
            RegexOptions.CultureInvariant);

        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(ExtensionModuleFitnessTests.RepositoryRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file =>
                !file.StartsWith(extensionRoot, StringComparison.OrdinalIgnoreCase) &&
                !file.StartsWith(registrationRoot, StringComparison.OrdinalIgnoreCase) &&
                !file.Equals(moduleList, StringComparison.OrdinalIgnoreCase) &&
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(ExtensionModuleFitnessTests.RepositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
        Assert.Contains(
            FlatContainerExtensionId,
            File.ReadAllText(moduleList) + File.ReadAllText(
                Path.Combine(extensionRoot, "FlatContainerModule.cs")));
    }

    [Fact]
    public async Task Flat_container_reads_are_audited_against_the_extracted_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Audited.Package", "1.0.0").Build());
        await store.AddSymbolAsync(TestPackageBuilder.Create("Audited.Package", "1.0.0")
            .WithFile("lib/net10.0/Audited.Package.pdb", [1, 2, 3, 4])
            .Build()
            .Content);

        await DispatchAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
            host,
            "NuGet.FlatContainer.GetVersions",
            new GetPackageVersionsRequest("audited.package"));
        await DispatchAsync<GetSymbolRequest, GetSymbolResponse>(
            host,
            "NuGet.FlatContainer.GetSymbol",
            new GetSymbolRequest(new PackageIdentity("Audited.Package", "1.0.0")));

        var audits = host.Services.GetRequiredService<CapabilityAuditLog>().Entries;
        Assert.Contains(
            audits,
            entry => entry.OwnerId == FlatContainerExtensionId &&
                     entry.OperationId == "NuGet.FlatContainer.GetVersions" &&
                     entry.CapabilityName == "packages.metadata.read" &&
                     entry.Outcome == CapabilityCallOutcome.Succeeded);
        Assert.Contains(
            audits,
            entry => entry.OwnerId == FlatContainerExtensionId &&
                     entry.OperationId == "NuGet.FlatContainer.GetSymbol" &&
                     entry.CapabilityName == SymbolsCapability &&
                     entry.Outcome == CapabilityCallOutcome.Succeeded);
        Assert.DoesNotContain(
            audits,
            entry => entry.OwnerId == ProtocolExtensionId &&
                     entry.OperationId!.StartsWith("NuGet.FlatContainer.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PackageModerationState.Published, true, true)]
    [InlineData(PackageModerationState.Published, false, true)]
    [InlineData(PackageModerationState.Quarantined, true, false)]
    [InlineData(PackageModerationState.Rejected, true, false)]
    [InlineData(PackageModerationState.Deleted, true, false)]
    public async Task Flat_container_reads_apply_authoritative_visibility_before_responding(
        PackageModerationState state,
        bool listed,
        bool readable)
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        var package = TestPackageBuilder.Create("Visibility.Flat", "1.0.0").Build();
        await store.AddAsync(package);
        await store.AddSymbolAsync(TestPackageBuilder.Create("Visibility.Flat", "1.0.0")
            .WithFile("lib/net10.0/Visibility.Flat.pdb", [9, 9, 9, 9])
            .Build()
            .Content);
        if (!listed)
        {
            Assert.True(await store.SetListedAsync("Visibility.Flat", "1.0.0", false));
        }

        if (state != PackageModerationState.Published)
        {
            Assert.True(await store.SetModerationStateAsync("Visibility.Flat", "1.0.0", state));
        }

        var identity = new PackageIdentity("Visibility.Flat", "1.0.0");
        var versions = await DispatchAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
            host,
            "NuGet.FlatContainer.GetVersions",
            new GetPackageVersionsRequest("visibility.flat"));
        var content = await DispatchAsync<GetPackageRequest, GetPackageResponse>(
            host,
            "NuGet.FlatContainer.GetPackage",
            new GetPackageRequest(identity));
        var nuspec = await DispatchAsync<GetNuspecRequest, GetNuspecResponse>(
            host,
            "NuGet.FlatContainer.GetNuspec",
            new GetNuspecRequest(identity));
        var hash = await DispatchAsync<GetPackageHashRequest, GetPackageHashResponse>(
            host,
            "NuGet.FlatContainer.GetHash",
            new GetPackageHashRequest(identity));
        var symbols = await DispatchAsync<GetSymbolRequest, GetSymbolResponse>(
            host,
            "NuGet.FlatContainer.GetSymbol",
            new GetSymbolRequest(identity));

        if (readable)
        {
            Assert.Equal(["1.0.0"], versions.Value!.Versions.ToArray());
            Assert.Equal(package.ContentLength, content.Value!.Package.Length);
            Assert.Equal(package.PackageHash, content.Value.Package.Sha512);
            Assert.True(content.Value.Package.SupportsRanges);
            Assert.True(nuspec.Value!.Nuspec.Length > 0);
            Assert.Equal(package.PackageHash, hash.Value!.Sha512);
            Assert.True(symbols.Value!.Symbols.Length > 0);
        }
        else
        {
            Assert.All(
                new[]
                {
                    versions.Error,
                    content.Error,
                    nuspec.Error,
                    hash.Error,
                    symbols.Error
                },
                error => Assert.Equal(OperationErrorKind.NotFound, error!.Kind));
        }
    }

    [Fact]
    public async Task Extracted_package_content_is_a_bounded_kernel_lease_released_on_end_of_stream()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        var package = TestPackageBuilder.Create("Streamed.Flat", "1.0.0")
            .WithFile("lib/net10.0/payload.bin", new string('p', 64 * 1024))
            .Build();
        await store.AddAsync(package);
        var execution = new OperationExecutionContext("flat-container-test");

        var response = await host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<GetPackageRequest, GetPackageResponse>(
                new OperationId("NuGet.FlatContainer.GetPackage"),
                new GetPackageRequest(new PackageIdentity("Streamed.Flat", "1.0.0")),
                execution,
                CancellationToken.None);

        var descriptor = response.Value!.Package;
        var content = execution.Content.Resolve(descriptor.Content);
        Assert.NotNull(content.Stream);
        Assert.Equal(package.ContentLength, descriptor.Length);
        Assert.Equal(package.ContentLength, descriptor.Content.MaximumLength);
        Assert.True(content.SupportsRanges);

        using var buffer = new MemoryStream();
        await content.Stream!.CopyToAsync(buffer);
        await content.Stream.DisposeAsync();

        Assert.Equal(package.ContentLength, buffer.Length);
        Assert.Contains(
            host.Services.GetRequiredService<CapabilityAuditLog>().Entries,
            entry => entry.OwnerId == FlatContainerExtensionId &&
                     entry.CapabilityName == "packages.content.read" &&
                     entry.Action == "consume-content" &&
                     entry.Outcome == CapabilityCallOutcome.Succeeded);
    }

    [Fact]
    public async Task Extracted_reads_honor_cancellation_before_touching_package_state()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Cancelled.Flat", "1.0.0").Build());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Services.GetRequiredService<OperationDispatcher>()
                .DispatchAsync<GetPackageRequest, GetPackageResponse>(
                    new OperationId("NuGet.FlatContainer.GetPackage"),
                    new GetPackageRequest(new PackageIdentity("Cancelled.Flat", "1.0.0")),
                    new OperationExecutionContext("flat-container-test"),
                    cancellation.Token)
                .AsTask());
    }

    [Fact]
    public void Symbol_reads_remain_registry_dispatched_without_a_public_route()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        Assert.Contains(
            "NuGet.FlatContainer.GetSymbol",
            KernelOperationRoutes.NonRoutedOperations);
        Assert.DoesNotContain(
            host.Graph.Endpoints.SelectMany(endpoint =>
                endpoint.Descriptor.Operations.Select(operation => operation.OperationId)),
            operationId => operationId == "NuGet.FlatContainer.GetSymbol");
        Assert.Equal(
            FlatContainerExtensionId,
            host.Registry.Find("NuGet.FlatContainer.GetSymbol")!.ExtensionId);
    }

    private static ValueTask<OperationResponse<TResponse>> DispatchAsync<TRequest, TResponse>(
        TestServerApplication host,
        string operationId,
        TRequest request) =>
        host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<TRequest, TResponse>(
                new OperationId(operationId),
                request,
                new OperationExecutionContext("flat-container-test"),
                CancellationToken.None);
}
