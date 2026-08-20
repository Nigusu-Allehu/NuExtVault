using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class OperationOwnerTests
{
    [Fact]
    public async Task Routed_and_non_routed_operations_share_one_typed_dispatch_path()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Owner.Example", "1.0.0").Build());

        var versions = await DispatchAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
            host,
            "NuGet.FlatContainer.GetVersions",
            new GetPackageVersionsRequest("owner.example"));
        var listed = await DispatchAsync<ListPackagesRequest, ListPackagesResponse>(
            host,
            "NuGet.PackageManagement.List",
            new ListPackagesRequest("Owner.Example", 0, 20));

        Assert.Equal(["1.0.0"], versions.Value!.Versions.ToArray());
        var summary = Assert.Single(listed.Value!.Packages);
        Assert.Equal("Owner.Example", summary.Package.Id);
        Assert.Equal("1.0.0", summary.Package.Version);
        Assert.True(summary.Listed);
    }

    [Fact]
    public async Task Missing_resources_return_typed_not_found_errors()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        var response = await DispatchAsync<GetPackageHashRequest, GetPackageHashResponse>(
            host,
            "NuGet.FlatContainer.GetHash",
            new GetPackageHashRequest(new PackageIdentity("Missing.Package", "1.0.0")));

        Assert.Null(response.Value);
        Assert.Equal(OperationErrorKind.NotFound, response.Error!.Kind);
    }

    [Fact]
    public async Task Symbol_reads_are_served_as_content_without_extra_copies()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        var symbols = TestPackageBuilder.Create("Symbol.Example", "1.0.0")
            .WithFile("lib/net10.0/Symbol.Example.pdb", [1, 2, 3, 4])
            .Build();
        await store.AddAsync(TestPackageBuilder.Create("Symbol.Example", "1.0.0").Build());
        await store.AddSymbolAsync(symbols.Content);
        var execution = new OperationExecutionContext("owner-test");

        var response = await host.Services
            .GetRequiredService<OperationDispatcher>()
            .DispatchAsync<GetSymbolRequest, GetSymbolResponse>(
                new OperationId("NuGet.FlatContainer.GetSymbol"),
                new GetSymbolRequest(new PackageIdentity("Symbol.Example", "1.0.0")),
                execution,
                CancellationToken.None);

        var descriptor = response.Value!.Symbols;
        var content = execution.Content.Resolve(descriptor.Content);
        Assert.Equal(symbols.Content.Length, descriptor.Length);
        Assert.Equal(symbols.Content, content.Bytes!.Value.ToArray());
    }

    [Fact]
    public async Task Diagnostics_operation_reports_host_counters()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        var response = await DispatchAsync<GetDiagnosticsRequest, GetDiagnosticsResponse>(
            host,
            "NuTest.Diagnostics.Get",
            new GetDiagnosticsRequest());

        Assert.NotNull(response.Value);
        Assert.Equal(0, response.Value!.FailedRequestCount);
    }

    [Fact]
    public async Task Backup_and_restore_require_durable_storage()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var execution = new OperationExecutionContext("owner-test");
        var destination = execution.Content.RegisterFile(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            "application/zip");

        var response = await host.Services
            .GetRequiredService<OperationDispatcher>()
            .DispatchAsync<CreateBackupRequest, CreateBackupResponse>(
                new OperationId("NuTest.Backup.Create"),
                new CreateBackupRequest(destination, "tests"),
                execution,
                CancellationToken.None);

        Assert.Null(response.Value);
        Assert.Equal(OperationErrorKind.Unavailable, response.Error!.Kind);
    }

    [Fact]
    public async Task Control_operations_are_dispatched_through_the_registry()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Control.Example", "1.0.0").Build());

        var state = await DispatchAsync<GetControlStateRequest, GetControlStateResponse>(
            host,
            "NuTest.Control.GetState",
            new GetControlStateRequest());
        var unlisted = await DispatchAsync<UnlistControlPackageRequest, UnlistControlPackageResponse>(
            host,
            "NuTest.Control.UnlistPackage",
            new UnlistControlPackageRequest(new PackageIdentity("Control.Example", "1.0.0")));
        var stored = await store.FindStoredAsync("Control.Example", "1.0.0");

        Assert.Equal(1, state.Value!.PackageCount);
        Assert.Equal("Control.Example", unlisted.Value!.Package.Id);
        Assert.False(stored!.IsListed);
    }

    private static ValueTask<OperationResponse<TResponse>> DispatchAsync<TRequest, TResponse>(
        TestServerApplication host,
        string operationId,
        TRequest request) =>
        host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<TRequest, TResponse>(
                new OperationId(operationId),
                request,
                new OperationExecutionContext("owner-test"),
                CancellationToken.None);
}
