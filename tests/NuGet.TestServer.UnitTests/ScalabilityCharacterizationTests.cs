using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class ScalabilityCharacterizationTests
{
    private const int WarmupIterations = 1000;
    private const int SampleCount = 30;
    private const int OperationsPerSample = 1000;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Record_step_11d_release_characterization()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NUGET_TESTSERVER_RUN_PERF"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Scale.Metadata", "1.0.0").Build());
        var dispatcher = host.Services.GetRequiredService<OperationDispatcher>();
        var registration = dispatcher.Registry.Find(OperationIds.FlatContainerGetVersions)!;

        Func<ValueTask> storeDirect = async () =>
        {
            _ = await store.FindByIdAsync("Scale.Metadata", CancellationToken.None);
        };
        Func<ValueTask> ownerDirect = async () =>
        {
            var execution = new OperationExecutionContext(
                Guid.NewGuid().ToString("N"),
                UnrestrictedOperationAuthorization.Instance);
            _ = await registration.Invoke(
                new GetPackageVersionsRequest("Scale.Metadata"),
                execution,
                CancellationToken.None);
        };
        Func<ValueTask> gateway = async () =>
        {
            var execution = new OperationExecutionContext(
                Guid.NewGuid().ToString("N"),
                UnrestrictedOperationAuthorization.Instance);
            _ = await dispatcher.DispatchAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
                new OperationId(OperationIds.FlatContainerGetVersions),
                new GetPackageVersionsRequest("Scale.Metadata"),
                execution,
                CancellationToken.None);
        };

        await WarmAsync(storeDirect);
        await WarmAsync(ownerDirect);
        await WarmAsync(gateway);
        var storeDirectResult = await MeasureAsync(storeDirect);
        var ownerDirectResult = await MeasureAsync(ownerDirect);
        var gatewayResult = await MeasureAsync(gateway);
        var startup = MeasureStartup();
        var parallelHosts = await MeasureParallelHostsAsync();
        var audit = MeasureAudit();
        var health = MeasureReadiness();
        var catalog = new Dictionary<int, Measurement>();
        foreach (var count in new[] { 8, 50, 200 })
        {
            catalog[count] = MeasureCatalog(count);
        }

        var result = new
        {
            step = "11D",
            timestampUtc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            configuration = IsRelease() ? "Release" : "Debug",
            methodology = new
            {
                warmupIterations = WarmupIterations,
                sampleCount = SampleCount,
                operationsPerSample = OperationsPerSample,
                latency = "Stopwatch elapsed time divided by completed operations; p95 over samples.",
                allocations = "GC.GetTotalAllocatedBytes delta divided by completed operations.",
                baseline = "Direct store characterizes the complete abstraction path. Direct owner invocation is the closest equivalent baseline for dispatcher/registry overhead; both owner and gateway paths include capability, visibility, and audit."
            },
            metadataRead = new
            {
                storeDirect = storeDirectResult,
                ownerDirect = ownerDirectResult,
                gateway = gatewayResult,
                p95OverheadPercent =
                    ((gatewayResult.P95Microseconds / ownerDirectResult.P95Microseconds) - 1) * 100,
                allocationOverheadBytes =
                    gatewayResult.AllocatedBytesPerOperation -
                    ownerDirectResult.AllocatedBytesPerOperation
            },
            embeddedCompositionStartup = startup,
            parallelEmbeddedHosts = parallelHosts,
            readinessWithOneThousandInventoryFiles = health,
            capabilityAudit = audit,
            extensionState = new
            {
                lockStripes = ExtensionStateStore.LockStripeCount,
                currentBuffering = "Payload JSON, base64 payload, and envelope JSON are materialized per record.",
                blockersForStep12A = new[]
                {
                    "Per-owner and per-record quotas",
                    "Optimistic concurrency tokens",
                    "Schema migrations",
                    "Streaming record I/O",
                    "Checkpoint and crash-safe restore participation"
                }
            },
            catalogResolution = catalog
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        var output = Environment.GetEnvironmentVariable("NUGET_TESTSERVER_PERF_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, json);
        }

        Console.WriteLine(json);
    }

    private static async Task WarmAsync(Func<ValueTask> operation)
    {
        for (var index = 0; index < WarmupIterations; index++)
        {
            await operation();
        }
    }

    private static async Task<Measurement> MeasureAsync(Func<ValueTask> operation)
    {
        var samples = new double[SampleCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var operationIndex = 0; operationIndex < OperationsPerSample; operationIndex++)
            {
                await operation();
            }

            samples[sample] = Stopwatch.GetElapsedTime(started).TotalMicroseconds /
                              OperationsPerSample;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return Summarize(samples, allocated / (double)(SampleCount * OperationsPerSample));
    }

    private static Measurement MeasureStartup()
    {
        const int startupSamples = 20;
        var samples = new double[startupSamples];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            using var host = TestServerApplication.Build(ServerProfiles.Embedded);
            samples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000;
        }

        return Summarize(
            samples,
            (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)startupSamples);
    }

    private static Measurement MeasureAudit()
    {
        const int operations = 100_000;
        var audit = new CapabilityAuditLog();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            audit.Record(
                "host",
                "owner",
                "packages.metadata.read",
                "find",
                CapabilityCallOutcome.Succeeded);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMicroseconds / operations;
        return new Measurement(
            elapsed,
            elapsed,
            elapsed,
            (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)operations);
    }

    private static Measurement MeasureReadiness()
    {
        using var directory = new TemporaryDirectory();
        var inventory = Path.Combine(directory.Path, "packages");
        Directory.CreateDirectory(inventory);
        for (var index = 0; index < 1000; index++)
        {
            File.WriteAllText(Path.Combine(inventory, $"{index:D4}.nupkg"), string.Empty);
        }

        var health = new StorageHealth(directory.Path);
        var samples = new double[SampleCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = health.GetReadiness();
            samples[sample] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        return Summarize(
            samples,
            (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)SampleCount);
    }

    private static async Task<ParallelHostMeasurement> MeasureParallelHostsAsync()
    {
        const int hostCount = 100;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var hosts = await Task.WhenAll(Enumerable.Range(0, hostCount).Select(
            _ => Task.Run(() => TestServerApplication.Build(ServerProfiles.Embedded))));
        var elapsed = Stopwatch.GetElapsedTime(started);
        var retained = GC.GetTotalMemory(forceFullCollection: true) - memoryBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        foreach (var host in hosts)
        {
            host.Dispose();
        }

        return new ParallelHostMeasurement(
            hostCount,
            elapsed.TotalMilliseconds,
            allocated / (double)hostCount,
            retained / (double)hostCount);
    }

    private static Measurement MeasureCatalog(int count)
    {
        var manifests = Enumerable.Range(0, count)
            .Select(index =>
            {
                var operation = $"scale.operation.{index:D3}";
                return new ExtensionManifest(
                    1,
                    $"scale.extension.{index:D3}",
                    new ExtensionVersion(1, 0, 0),
                    ExtensionVersionRange.Major(1),
                    [],
                    [operation],
                    [
                        TestEndpointDescriptors.Endpoint(
                            $"scale.route.{index:D3}",
                            "GET",
                            $"/scale/{index:D3}",
                            operation)
                    ],
                    [],
                    []);
            })
            .ToArray();
        var profile = new ServerProfile(
            "scale",
            ServerProfileKind.Embedded,
            [.. manifests.Select(manifest => new ExtensionSelection(manifest.Id, []))],
            []);
        var samples = new double[SampleCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = new ExtensionCatalog(manifests).ResolveWith(profile);
            samples[sample] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        return Summarize(
            samples,
            (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)SampleCount);
    }

    private static Measurement Summarize(double[] samples, double allocatedBytesPerOperation)
    {
        Array.Sort(samples);
        return new Measurement(
            samples.Average(),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            allocatedBytesPerOperation);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static bool IsRelease()
    {
#if DEBUG
        return false;
#else
        return true;
#endif
    }

    public sealed record Measurement(
        double MeanMicroseconds,
        double P50Microseconds,
        double P95Microseconds,
        double AllocatedBytesPerOperation);

    public sealed record ParallelHostMeasurement(
        int HostCount,
        double TotalMilliseconds,
        double AllocatedBytesPerHost,
        double RetainedManagedBytesPerHost);
}
