using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Data.Sqlite;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class OperationDispatcherTests
{
    private static readonly OperationId SearchOperation = new("NuGet.Search.Query");
    private static readonly OperationId PackageOperation = new("NuGet.FlatContainer.GetPackage");

    [Fact]
    public async Task Dispatch_returns_the_declared_typed_response()
    {
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (request, _, _) => ValueTask.FromResult(
                    OperationResponse<SearchResponse>.Success(
                        new SearchResponse(request.Take, [])))));

        var response = await fixture.Dispatcher.DispatchAsync<SearchRequest, SearchResponse>(
            SearchOperation,
            new SearchRequest("json", 0, 7, false, null),
            fixture.Execution,
            CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Equal(7, response.Value!.TotalHits);
    }

    [Fact]
    public async Task Unknown_operations_return_an_internal_error()
    {
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (_, _, _) => ValueTask.FromResult(
                    OperationResponse<SearchResponse>.Success(new SearchResponse(0, [])))));

        var response = await fixture.Dispatcher
            .DispatchAsync<GetPackageHashRequest, GetPackageHashResponse>(
                new OperationId("NuGet.FlatContainer.GetHash"),
                new GetPackageHashRequest(new PackageIdentity("Example", "1.0.0")),
                fixture.Execution,
                CancellationToken.None);

        Assert.Null(response.Value);
        Assert.Equal(OperationErrorKind.Internal, response.Error!.Kind);
        Assert.Contains("NuGet.FlatContainer.GetHash", response.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_rejects_requests_that_do_not_match_the_declared_contract()
    {
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (_, _, _) => ValueTask.FromResult(
                    OperationResponse<SearchResponse>.Success(new SearchResponse(0, [])))));

        var response = await fixture.Dispatcher
            .DispatchAsync<GetPackageHashRequest, GetPackageHashResponse>(
                SearchOperation,
                new GetPackageHashRequest(new PackageIdentity("Example", "1.0.0")),
                fixture.Execution,
                CancellationToken.None);

        Assert.Null(response.Value);
        Assert.Equal(OperationErrorKind.Internal, response.Error!.Kind);
        Assert.Contains("contract", response.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("limit", OperationErrorCodes.LimitExceeded)]
    [InlineData("invalid", OperationErrorCodes.InvalidRequest)]
    [InlineData("duplicate", OperationErrorCodes.Conflict)]
    public async Task Owner_exceptions_are_classified_into_typed_errors(string kind, string expectedCode)
    {
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (_, _, _) => throw CreateException(kind)));

        var response = await fixture.Dispatcher.DispatchAsync<SearchRequest, SearchResponse>(
            SearchOperation,
            new SearchRequest(string.Empty, 0, 20, false, null),
            fixture.Execution,
            CancellationToken.None);

        Assert.Null(response.Value);
        Assert.Equal(expectedCode, response.Error!.Code);
    }

    [Fact]
    public async Task Storage_failures_are_recorded_and_rethrown()
    {
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (_, _, _) => throw new IOException("disk offline")));

        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Dispatcher.DispatchAsync<SearchRequest, SearchResponse>(
                SearchOperation,
                new SearchRequest(string.Empty, 0, 20, false, null),
                fixture.Execution,
                CancellationToken.None));

        Assert.Equal(1, fixture.Diagnostics.StorageFailureCount);
    }

    [Fact]
    public async Task Dispatch_propagates_cancellation_to_the_owner()
    {
        var observed = false;
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                async (_, _, token) =>
                {
                    observed = true;
                    await Task.Delay(Timeout.Infinite, token);
                    return OperationResponse<SearchResponse>.Success(new SearchResponse(0, []));
                }));
        using var cancellation = new CancellationTokenSource();

        var dispatch = fixture.Dispatcher.DispatchAsync<SearchRequest, SearchResponse>(
            SearchOperation,
            new SearchRequest(string.Empty, 0, 20, false, null),
            fixture.Execution,
            cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(observed);
    }

    [Fact]
    public async Task Dispatch_does_not_invoke_owners_for_already_cancelled_requests()
    {
        var invoked = false;
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (_, _, _) =>
                {
                    invoked = true;
                    return ValueTask.FromResult(
                        OperationResponse<SearchResponse>.Success(new SearchResponse(0, [])));
                }));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Dispatcher.DispatchAsync<SearchRequest, SearchResponse>(
                SearchOperation,
                new SearchRequest(string.Empty, 0, 20, false, null),
                fixture.Execution,
                cancellation.Token));

        Assert.False(invoked);
    }

    [Fact]
    public async Task Content_responses_stream_without_buffering()
    {
        var content = new ThrowOnReadStream(4096);
        using var fixture = DispatcherFixture.Create(
            Owner<GetPackageRequest, GetPackageResponse>(
                PackageOperation.Value,
                (_, execution, _) =>
                {
                    var handle = execution.Content.RegisterStream(
                        content,
                        "application/octet-stream",
                        content.Length,
                        supportsRanges: true);
                    return ValueTask.FromResult(
                        OperationResponse<GetPackageResponse>.Success(
                            new GetPackageResponse(
                                new ContentDescriptor(handle, null, content.Length, true))));
                }),
            PackageOperation.Value);

        var response = await fixture.Dispatcher.DispatchAsync<GetPackageRequest, GetPackageResponse>(
            PackageOperation,
            new GetPackageRequest(new PackageIdentity("Example", "1.0.0")),
            fixture.Execution,
            CancellationToken.None);

        var descriptor = response.Value!.Package;
        var resolved = fixture.Execution.Content.Resolve(descriptor.Content);
        Assert.Same(content, resolved.Stream);
        Assert.Equal(4096, descriptor.Length);
        Assert.True(descriptor.SupportsRanges);
        Assert.False(content.WasRead);
    }

    [Fact]
    public void Content_handles_are_scoped_to_one_execution_and_host_instance()
    {
        var first = new OperationExecutionContext("host-a");
        var second = new OperationExecutionContext("host-a");
        var otherHost = new OperationExecutionContext("host-b");
        var handle = first.Content.RegisterBytes(
            Encoding.UTF8.GetBytes("payload"),
            "application/json");

        Assert.Equal("payload", Encoding.UTF8.GetString(first.Content.Resolve(handle).Bytes!.Value.Span));
        Assert.False(second.Content.TryResolve(handle, out _));
        Assert.False(otherHost.Content.TryResolve(handle, out _));
        Assert.Throws<InvalidOperationException>(() => second.Content.Resolve(handle));
        Assert.StartsWith("host-a:", handle.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_records_operation_attributed_diagnostics()
    {
        using var fixture = DispatcherFixture.Create(
            Owner<SearchRequest, SearchResponse>(
                SearchOperation.Value,
                (_, _, _) => ValueTask.FromResult(
                    OperationResponse<SearchResponse>.Success(new SearchResponse(0, [])))));
        var measurements = new List<(long Value, string Operation, string Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "NuGet.TestServer" &&
                instrument.Name == "nuget.server.operations")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "nuget.operation.id")
                {
                    operation = tag.Value?.ToString();
                }
                else if (tag.Key == "nuget.operation.outcome")
                {
                    outcome = tag.Value?.ToString();
                }
            }

            measurements.Add((value, operation ?? string.Empty, outcome ?? string.Empty));
        });
        listener.Start();

        await fixture.Dispatcher.DispatchAsync<SearchRequest, SearchResponse>(
            SearchOperation,
            new SearchRequest(string.Empty, 0, 20, false, null),
            fixture.Execution,
            CancellationToken.None);
        listener.RecordObservableInstruments();

        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(SearchOperation.Value, measurement.Operation);
        Assert.Equal("success", measurement.Outcome);
    }

    private static Exception CreateException(string kind) => kind switch
    {
        "limit" => new PackageLimitExceededException("too large"),
        "invalid" => new InvalidPackageException("bad package"),
        "duplicate" => new DuplicatePackageException("Example", "1.0.0"),
        _ => new SqliteException("boom", 1)
    };

    private static DelegateOperationOwner<TRequest, TResponse> Owner<TRequest, TResponse>(
        string operationId,
        Func<TRequest, OperationExecutionContext, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler) =>
        new(operationId, handler);

    private sealed class DispatcherFixture : IDisposable
    {
        private readonly InMemoryPackageStore _store;

        private DispatcherFixture(
            InMemoryPackageStore store,
            ServerDiagnostics diagnostics,
            OperationDispatcher dispatcher)
        {
            _store = store;
            Diagnostics = diagnostics;
            Dispatcher = dispatcher;
            Execution = new OperationExecutionContext("test-host");
        }

        public ServerDiagnostics Diagnostics { get; }

        public OperationDispatcher Dispatcher { get; }

        public OperationExecutionContext Execution { get; }

        public static DispatcherFixture Create<TRequest, TResponse>(
            DelegateOperationOwner<TRequest, TResponse> owner,
            string? operationId = null)
        {
            var graph = new ResolvedExtensionGraph(
                "test",
                [],
                [new ResolvedOperation(operationId ?? SearchOperation.Value, BuiltInExtensionIds.Protocol)],
                [],
                [],
                [],
                [],
                "profile=test\n");
            var registry = new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.Protocol, owner)
                .Build(graph);
            var store = new InMemoryPackageStore();
            var diagnostics = new ServerDiagnostics(store);
            return new DispatcherFixture(
                store,
                diagnostics,
                new OperationDispatcher(registry, diagnostics));
        }

        public void Dispose()
        {
            Diagnostics.Dispose();
            _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class ThrowOnReadStream(long length) : Stream
    {
        public bool WasRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length { get; } = length;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
