using System.Reflection;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Kernel.Owners;
using NuGet.TestServer.Extensions.Vulnerabilities;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class CapabilityBrokerTests
{
    [Fact]
    public void Undeclared_capabilities_are_denied_even_when_another_owner_is_granted()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var broker = host.Services.GetRequiredService<CapabilityBroker>();

        var exception = Assert.Throws<CapabilityDeniedException>(
            () => broker.ForOwner(BuiltInExtensionIds.Protocol)
                .GetRequired<IPackageMutationCapability>(BuiltInCapabilityNames.PackagesPublish));

        Assert.Equal(BuiltInExtensionIds.Protocol, exception.OwnerId);
        Assert.Equal(BuiltInCapabilityNames.PackagesPublish, exception.CapabilityName);
    }

    [Fact]
    public void Handles_are_bound_to_one_host_and_owner()
    {
        using var first = TestServerApplication.Build(ServerProfiles.Embedded);
        using var second = TestServerApplication.Build(ServerProfiles.Embedded);

        var firstHandle = first.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner(BuiltInExtensionIds.Protocol)
            .GetRequired<IPackageReadCapability>(BuiltInCapabilityNames.PackagesMetadataRead);
        var secondHandle = second.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner(BuiltInExtensionIds.Protocol)
            .GetRequired<IPackageReadCapability>(BuiltInCapabilityNames.PackagesMetadataRead);

        var firstIdentity = Assert.IsAssignableFrom<ICapabilityHandleIdentity>(firstHandle);
        var secondIdentity = Assert.IsAssignableFrom<ICapabilityHandleIdentity>(secondHandle);
        Assert.Equal(BuiltInExtensionIds.Protocol, firstIdentity.OwnerId);
        Assert.NotEqual(firstIdentity.HostInstanceId, secondIdentity.HostInstanceId);
        Assert.NotSame(firstHandle, secondHandle);
    }

    [Fact]
    public async Task Capability_calls_honor_cancellation_and_are_audited()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var broker = host.Services.GetRequiredService<CapabilityBroker>();
        var packages = broker.ForOwner(BuiltInExtensionIds.Protocol)
            .GetRequired<IPackageReadCapability>(BuiltInCapabilityNames.PackagesMetadataRead);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => packages.GetAllAsync(cancellation.Token).AsTask());

        var entry = Assert.Single(
            host.Services.GetRequiredService<CapabilityAuditLog>().Entries,
            item => item.OwnerId == BuiltInExtensionIds.Protocol &&
                    item.CapabilityName == BuiltInCapabilityNames.PackagesMetadataRead);
        Assert.Equal(CapabilityCallOutcome.Cancelled, entry.Outcome);
        Assert.Equal(
            host.Services.GetRequiredService<ServerComposition>().InstanceId,
            entry.HostInstanceId);
    }

    [Fact]
    public async Task Privileged_calls_are_attributed_to_the_dispatched_operation()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<Packages.IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Audit.Example", "1.0.0").Build());

        await host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<UnlistPackageRequest, UnlistPackageResponse>(
                new OperationId("NuGet.PackageManagement.Unlist"),
                new UnlistPackageRequest(
                    new PackageIdentity("Audit.Example", "1.0.0"),
                    "tests"),
                new OperationExecutionContext("audit-test"),
                CancellationToken.None);

        var entry = Assert.Single(
            host.Services.GetRequiredService<CapabilityAuditLog>().Entries,
            item => item.CapabilityName == BuiltInCapabilityNames.PackagesUnlist);
        Assert.Equal(BuiltInExtensionIds.Publication, entry.OwnerId);
        Assert.Equal("NuGet.PackageManagement.Unlist", entry.OperationId);
        Assert.Equal(CapabilityCallOutcome.Succeeded, entry.Outcome);
    }

    [Fact]
    public async Task Capability_concurrency_quotas_fail_closed()
    {
        var audit = new CapabilityAuditLog();
        var gate = new CapabilityCallGate(
            "host",
            "owner",
            "test.capability",
            audit,
            new CapabilityLimits(MaximumConcurrentCalls: 1, MaximumStreamBytes: 4));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.InvokeAsync(
            "hold",
            async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return true;
            },
            CancellationToken.None).AsTask();
        await entered.Task;

        await Assert.ThrowsAsync<CapabilityQuotaExceededException>(
            () => gate.InvokeAsync("second", _ => ValueTask.FromResult(true), CancellationToken.None)
                .AsTask());

        release.SetResult();
        await first;
    }

    [Fact]
    public void Capability_streams_are_bounded_and_do_not_buffer()
    {
        var source = new TrackingMemoryStream(new byte[5]);

        var exception = Assert.Throws<CapabilityStreamLimitExceededException>(
            () => CapabilityStreams.Bound(source, declaredLength: 5, maximumLength: 4));

        Assert.False(source.WasRead);
        Assert.Equal(5, exception.DeclaredLength);
        Assert.Equal(4, exception.MaximumLength);
    }

    [Fact]
    public async Task Capability_streams_enforce_actual_bytes_and_cancellation()
    {
        await using var oversized = CapabilityStreams.Bound(
            new MemoryStream(new byte[5]),
            declaredLength: 0,
            maximumLength: 4);
        var buffer = new byte[5];

        await Assert.ThrowsAsync<CapabilityStreamLimitExceededException>(
            () => oversized.ReadAsync(buffer).AsTask());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var cancelled = CapabilityStreams.Bound(
            new MemoryStream(new byte[1]),
            declaredLength: 1,
            maximumLength: 4,
            cancellation.Token,
            null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.ReadAsync(buffer).AsTask());

        var source = new TrackingMemoryStream(new byte[1]);
        var audit = new CapabilityAuditLog();
        var gate = new CapabilityCallGate(
            "host",
            "owner",
            "stream",
            audit,
            new CapabilityLimits());
        Assert.ThrowsAny<OperationCanceledException>(
            () => gate.LeaseStream("open", source, 1, cancellation.Token));
        Assert.True(source.WasDisposed);
    }

    [Fact]
    public void Capability_audit_retention_is_bounded()
    {
        var audit = new CapabilityAuditLog();

        for (var index = 0; index < 5000; index++)
        {
            audit.Record(
                "host",
                "owner",
                "capability",
                "action",
                CapabilityCallOutcome.Succeeded);
        }

        Assert.Equal(4096, audit.Entries.Count);
        Assert.Equal(904, audit.DroppedCount);
    }

    [Fact]
    public async Task Outbound_http_is_host_allowlisted()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Standard);
        var outbound = host.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner(BuiltInExtensionIds.Vulnerabilities)
            .GetRequired<IOutboundHttpCapability>(BuiltInCapabilityNames.OutboundHttp);

        await Assert.ThrowsAsync<CapabilityDeniedException>(
            () => outbound.SendAsync(
                new OutboundHttpRequest(
                    new Uri("https://example.com/"),
                    "GET",
                    ImmutableDictionary<string, string>.Empty,
                    1024),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Extension_state_is_owner_namespaced_atomic_and_integrity_protected()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExtensionStateStore(directory.Path);
        var grants = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            BuiltInCapabilityNames.ExtensionStateRead,
            BuiltInCapabilityNames.ExtensionStateWrite);
        var first = new ExtensionStateCapability(
            "host",
            "owner.first",
            grants,
            new CapabilityAuditLog(),
            new CapabilityLimits(),
            store);
        var second = new ExtensionStateCapability(
            "host",
            "owner.second",
            grants,
            new CapabilityAuditLog(),
            new CapabilityLimits(),
            store);

        await first.WriteAsync("snapshot", new StateValue("first"), CancellationToken.None);
        Assert.Equal("first", (await first.ReadAsync<StateValue>(
            "snapshot",
            CancellationToken.None))!.Value);
        Assert.Null(await second.ReadAsync<StateValue>("snapshot", CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.WriteAsync(
                "snapshot",
                new StateValue("cancelled"),
                cancellation.Token).AsTask());
        Assert.Equal("first", (await first.ReadAsync<StateValue>(
            "snapshot",
            CancellationToken.None))!.Value);

        var stateFile = Assert.Single(Directory.EnumerateFiles(
            directory.Path,
            "*.json",
            SearchOption.AllDirectories));
        await File.AppendAllTextAsync(stateFile, "corrupt");
        await Assert.ThrowsAsync<ExtensionStateException>(
            () => first.ReadAsync<StateValue>("snapshot", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Legacy_state_file_sets_are_logical_bounded_and_owner_scoped()
    {
        using var stateDirectory = new TemporaryDirectory();
        using var legacyDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(legacyDirectory.Path, "snapshot"));
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory.Path, "snapshot", "metadata.json"),
            "metadata");
        var registrations = ImmutableDictionary<
                string,
                ImmutableDictionary<string, LegacyStateFileSetRegistration>>
            .Empty.Add(
                "owner.first",
                ImmutableDictionary<string, LegacyStateFileSetRegistration>.Empty.Add(
                    "legacy",
                    new LegacyStateFileSetRegistration(
                        legacyDirectory.Path,
                        MaximumFileBytes: 16,
                        MaximumTotalBytes: 16).Validate()));
        var store = new ExtensionStateStore(stateDirectory.Path, registrations);
        var grants = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            BuiltInCapabilityNames.ExtensionStateRead);
        var first = new ExtensionStateCapability(
            "host",
            "owner.first",
            grants,
            new CapabilityAuditLog(),
            new CapabilityLimits(),
            store);
        var second = new ExtensionStateCapability(
            "host",
            "owner.second",
            grants,
            new CapabilityAuditLog(),
            new CapabilityLimits(),
            store);

        var fileSet = await first.ReadLegacyFileSetAsync("legacy", CancellationToken.None);

        var file = Assert.Single(fileSet!.Files);
        Assert.Equal("snapshot/metadata.json", file.LogicalName);
        Assert.Equal("metadata", System.Text.Encoding.UTF8.GetString(file.Content));
        Assert.Null(await second.ReadLegacyFileSetAsync("legacy", CancellationToken.None));
        await File.WriteAllBytesAsync(
            Path.Combine(legacyDirectory.Path, "snapshot", "oversized.json"),
            new byte[17]);
        await Assert.ThrowsAsync<CapabilityStreamLimitExceededException>(
            () => first.ReadLegacyFileSetAsync("legacy", CancellationToken.None).AsTask());
    }

    [Fact]
    public void Extension_state_capability_contract_exposes_no_filesystem_paths()
    {
        var contractTypes = new[]
        {
            typeof(IExtensionStateCapability),
            typeof(ExtensionStateFile),
            typeof(ExtensionStateFileSet)
        };

        Assert.DoesNotContain(
            contractTypes.SelectMany(type => type.GetMembers()),
            member => member.Name.Contains("Directory", StringComparison.OrdinalIgnoreCase) ||
                      member.Name.Contains("Root", StringComparison.OrdinalIgnoreCase) ||
                      member.Name.Contains("StoragePath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Degraded_extension_health_is_ready_but_visible_in_readiness()
    {
        var capability = new ServerOperationsCapability(
            "host",
            BuiltInExtensionIds.Operations,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                BuiltInCapabilityNames.OperationsQuery),
            new CapabilityAuditLog(),
            new CapabilityLimits(),
            new Operations.StorageHealth(storageDirectory: null),
            new ServerDiagnostics(new Packages.InMemoryPackageStore()),
            ServerHostingOptions.Create(
                ServerMode.Test,
                "http://127.0.0.1:0",
                Authentication.AuthenticationConfiguration.Anonymous,
                trustedProxies: null),
            storageDirectory: null,
            new StubExtensionHealthSource(
                new ExtensionHealthSnapshot(
                    Ready: true,
                    Status: "degraded",
                    Reason: "refresh failed")));

        var readiness = await capability.GetReadinessAsync(CancellationToken.None);

        Assert.True(readiness.Ready);
        Assert.Equal("degraded", readiness.Status);
        Assert.Equal("extensions", readiness.Dependency);
        Assert.Equal("refresh failed", readiness.Reason);
    }

    [Fact]
    public void Owner_adapters_receive_capabilities_not_privileged_implementation_objects()
    {
        Type[] forbidden =
        [
            typeof(IServiceProvider),
            typeof(Packages.IPackageStore),
            typeof(Packages.PackageSupplyChainService),
            typeof(Operations.StorageHealth),
            typeof(Faults.FaultRuleStore),
            typeof(Requests.RequestRecorder)
        ];
        Type[] owners =
        [
            typeof(ProtocolReadOperations),
            typeof(RegistrationSearchOperations),
            typeof(PublicationOperations),
            typeof(ModerationOperations),
            typeof(VulnerabilityOperations),
            typeof(ControlOperations),
            typeof(ServerOperationsOperations)
        ];

        foreach (var owner in owners)
        {
            var constructor = Assert.Single(owner.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            Assert.DoesNotContain(
                constructor.GetParameters(),
                parameter => forbidden.Contains(parameter.ParameterType));
        }

        var capabilityTypes = typeof(ICapabilityHandleIdentity).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(ICapabilityHandleIdentity).Namespace)
            .Where(type => type.IsInterface);
        Assert.DoesNotContain(
            capabilityTypes.SelectMany(type => type.GetProperties()),
            property => forbidden.Contains(property.PropertyType) ||
                        property.PropertyType == typeof(string) &&
                        property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IPackageReadCapability).GetMethods(),
            method => method.ReturnType.ToString().Contains(
                nameof(TestPackage),
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(CapabilityPackageMetadata).GetMembers(),
            member => member.Name is "Content" or "OpenReadStream");
    }

    [Fact]
    public void Built_in_owners_declare_exactly_the_capabilities_their_adapters_use()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var broker = host.Services.GetRequiredService<CapabilityBroker>();

        foreach (var owner in BuiltInOwnerCapabilityRequirements.All)
        {
            var context = broker.ForOwner(owner.Key);
            Assert.Equal(
                owner.Value.Order(StringComparer.Ordinal),
                context.GrantedCapabilities.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Embedded_and_production_profiles_deny_test_network_secret_and_sidecar_escalation()
    {
        Assert.Contains(
            ServerProfiles.Embedded.Grants,
            grant => grant.Name == BuiltInCapabilityNames.ControlFaultsInject);
        Assert.Contains(
            ServerProfiles.Embedded.Grants,
            grant => grant.Name == BuiltInCapabilityNames.ControlRequestsRead);
        Assert.DoesNotContain(
            ServerProfiles.Embedded.Grants,
            grant => grant.Name is BuiltInCapabilityNames.OutboundHttp
                or BuiltInCapabilityNames.SecretsResolveReference
                or BuiltInCapabilityNames.SidecarExecution);
        Assert.DoesNotContain(
            ServerProfiles.Production.Grants,
            grant => grant.Name is BuiltInCapabilityNames.ControlFaultsInject
                or BuiltInCapabilityNames.ControlRequestsRead
                or BuiltInCapabilityNames.SecretsResolveReference
                or BuiltInCapabilityNames.SidecarExecution);

        var unsafeProduction = ServerProfiles.Production with
        {
            Grants = ServerProfiles.Production.Grants.Add(
                new CapabilityGrant(BuiltInCapabilityNames.ControlFaultsInject))
        };
        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => BuiltInExtensionCatalog.Instance.Resolve(unsafeProduction));
        Assert.Contains("production-capability-denied", exception.Message, StringComparison.Ordinal);

        var unsafeEmbedded = ServerProfiles.Embedded with
        {
            Grants = ServerProfiles.Embedded.Grants.Add(
                new CapabilityGrant(BuiltInCapabilityNames.OutboundHttp))
        };
        exception = Assert.Throws<ServerHostingConfigurationException>(
            () => BuiltInExtensionCatalog.Instance.Resolve(unsafeEmbedded));
        Assert.Contains("embedded-capability-denied", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasRead { get; private set; }
        public bool WasDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return base.Read(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

    }

    private sealed record StateValue(string Value);

    private sealed class StubExtensionHealthSource(ExtensionHealthSnapshot health)
        : IExtensionHealthSource
    {
        public ExtensionHealthSnapshot GetHealth() => health;
    }
}
