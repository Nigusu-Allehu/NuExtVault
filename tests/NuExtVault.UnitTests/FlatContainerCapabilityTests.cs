using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NuExtVault.Extensions;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.FlatContainer;
using NuExtVault.Hosting;
using NuExtVault.Kernel;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Packages;
using NuExtVault.Extensions.Official;

namespace NuExtVault.UnitTests;

/// <summary>
/// Step 13 capability gates. The extracted owner reaches package state only through
/// narrow, action-scoped capabilities that apply authoritative visibility themselves,
/// bound every stream, and stay denied for owners that did not request them.
/// </summary>
public sealed class FlatContainerCapabilityTests
{
    [Fact]
    public void The_flat_container_module_is_contributed_through_the_generic_module_seam()
    {
        var module = Assert.Single(
            OfficialExtensionModules.All,
            candidate => candidate.Contribution.Manifest.Id == FlatContainerModule.ExtensionId);

        Assert.IsType<FlatContainerModule>(module);
        Assert.Empty(module.Contribution.Contracts);
        Assert.Equal(
            FlatContainerModule.ExtensionId,
            module.Contribution.Selection.Id);
        Assert.Contains(
            BuiltInExtensionCatalog.Manifests,
            manifest => manifest.Id == FlatContainerModule.ExtensionId);
        Assert.Equal(
            OfficialExtensionModules.Manifests.Select(manifest => manifest.Id),
            OfficialExtensionModules.All.Select(candidate => candidate.Contribution.Manifest.Id));
    }

    [Fact]
    public void The_flat_container_owner_receives_only_narrow_read_capabilities()
    {
        var constructor = Assert.Single(typeof(FlatContainerOperations).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.Equal(
            [
                typeof(IPackageMetadataReadCapability),
                typeof(IPackageContentReadCapability),
                typeof(IPackageSymbolReadCapability)
            ],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(FlatContainerOperations)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)),
            type => type == typeof(OperationExecutionContext) ||
                    type == typeof(Stream) ||
                    type == typeof(IPackageStore));
    }

    [Fact]
    public void Capability_handles_are_scoped_to_the_flat_container_owner_and_deny_everything_else()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var broker = host.Services.GetRequiredService<CapabilityBroker>();
        var owner = broker.ForOwner(FlatContainerModule.ExtensionId);

        var symbols = owner.GetRequired<IPackageSymbolReadCapability>(
            BuiltInCapabilityNames.PackagesSymbolsRead);

        Assert.Equal(
            FlatContainerModule.ExtensionId,
            Assert.IsAssignableFrom<ICapabilityHandleIdentity>(symbols).OwnerId);
        Assert.Throws<CapabilityDeniedException>(() =>
            owner.GetRequired<IPackageMutationCapability>(BuiltInCapabilityNames.PackagesPublish));
        Assert.Throws<CapabilityDeniedException>(() =>
            owner.GetRequired<IPackageSymbolReadCapability>(
                BuiltInCapabilityNames.PackagesContentRead));
        Assert.Throws<CapabilityDeniedException>(() =>
            broker.ForOwner(BuiltInExtensionIds.Protocol)
                .GetRequired<IPackageSymbolReadCapability>(
                    BuiltInCapabilityNames.PackagesSymbolsRead));
    }

    [Fact]
    public async Task Narrow_read_capabilities_apply_visibility_themselves()
    {
        await using var store = new InMemoryPackageStore();
        var package = TestPackageBuilder.Create("Capability.Flat", "1.0.0").Build();
        await store.AddAsync(package);
        await store.AddSymbolAsync(TestPackageBuilder.Create("Capability.Flat", "1.0.0")
            .WithFile("lib/net10.0/Capability.Flat.pdb", [4, 5, 6, 7])
            .Build()
            .Content);
        var capability = Create(store);
        using var execution = OperationExecutionScope.Enter(
            new OperationExecutionContext("capability-test"));

        Assert.Equal(
            ["1.0.0"],
            (await capability.GetReadableVersionsAsync("capability.flat", CancellationToken.None))
                .ToArray());
        Assert.NotNull(await capability.OpenPackageAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));
        Assert.NotNull(await capability.OpenNuspecAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));
        Assert.Equal(
            package.PackageHash,
            await capability.GetPackageHashAsync("Capability.Flat", "1.0.0", CancellationToken.None));
        Assert.NotNull(await capability.OpenSymbolsAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));

        Assert.True(await store.SetModerationStateAsync(
            "Capability.Flat",
            "1.0.0",
            PackageModerationState.Quarantined));

        Assert.Empty(
            await capability.GetReadableVersionsAsync("capability.flat", CancellationToken.None));
        Assert.Null(await capability.OpenPackageAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));
        Assert.Null(await capability.OpenNuspecAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));
        Assert.Null(await capability.GetPackageHashAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));
        Assert.Null(await capability.OpenSymbolsAsync(
            "Capability.Flat",
            "1.0.0",
            CancellationToken.None));
    }

    [Fact]
    public async Task Content_and_symbol_reads_fail_closed_above_the_declared_stream_limit()
    {
        await using var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("Bounded.Flat", "1.0.0").Build());
        await store.AddSymbolAsync(TestPackageBuilder.Create("Bounded.Flat", "1.0.0")
            .WithFile("lib/net10.0/Bounded.Flat.pdb", [1, 2, 3, 4])
            .Build()
            .Content);
        var capability = Create(store, new CapabilityLimits(MaximumStreamBytes: 64));
        using var execution = OperationExecutionScope.Enter(
            new OperationExecutionContext("capability-test"));

        await Assert.ThrowsAsync<CapabilityStreamLimitExceededException>(() =>
            capability.OpenPackageAsync("Bounded.Flat", "1.0.0", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<CapabilityStreamLimitExceededException>(() =>
            capability.OpenSymbolsAsync("Bounded.Flat", "1.0.0", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Content_handles_never_escape_the_execution_that_created_them()
    {
        await using var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("Scoped.Flat", "1.0.0").Build());
        var capability = Create(store);
        var execution = new OperationExecutionContext("capability-test");
        ContentDescriptor? descriptor;
        using (OperationExecutionScope.Enter(execution))
        {
            descriptor = await capability.OpenPackageAsync(
                "Scoped.Flat",
                "1.0.0",
                CancellationToken.None);
        }

        Assert.NotNull(descriptor);
        Assert.NotNull(execution.Content.Resolve(descriptor!.Content).Stream);
        Assert.Throws<InvalidOperationException>(() =>
            new OperationExecutionContext("other").Content.Resolve(descriptor.Content));
    }

    private static PackageResourceReadCapability Create(
        InMemoryPackageStore store,
        CapabilityLimits? limits = null) =>
        new(
            "host",
            FlatContainerModule.ExtensionId,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                BuiltInCapabilityNames.PackagesIdentityRead,
                BuiltInCapabilityNames.PackagesMetadataRead,
                BuiltInCapabilityNames.PackagesContentRead,
                BuiltInCapabilityNames.PackagesSymbolsRead),
            new CapabilityAuditLog(),
            limits ?? new CapabilityLimits(),
            store,
            new PackageCandidateReader(store),
            PackageVisibilityPolicy.Instance);
}
