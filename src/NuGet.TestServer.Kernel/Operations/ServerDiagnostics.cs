using System.Diagnostics;
using System.Diagnostics.Metrics;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Operations;

public sealed class ServerDiagnostics : IDisposable
{
    private readonly Meter _meter = new("NuGet.TestServer");
    private readonly ActivitySource _activities = new("NuGet.TestServer");
    private readonly Counter<long> _requests;
    private readonly Counter<long> _errors;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _packagesPublished;
    private readonly Counter<long> _storageFailures;
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _operationDuration;
    private long _requestCount;
    private long _failedRequestCount;
    private long _publishedPackageCount;
    private long _storageFailureCount;

    public ServerDiagnostics(IPackageStore packages)
    {
        _requests = _meter.CreateCounter<long>("nuget.server.requests");
        _errors = _meter.CreateCounter<long>("nuget.server.errors");
        _requestDuration = _meter.CreateHistogram<double>(
            "nuget.server.request.duration",
            "ms");
        _packagesPublished = _meter.CreateCounter<long>("nuget.server.packages.published");
        _storageFailures = _meter.CreateCounter<long>("nuget.server.storage.failures");
        _operations = _meter.CreateCounter<long>("nuget.server.operations");
        _operationDuration = _meter.CreateHistogram<double>(
            "nuget.server.operation.duration",
            "ms");
        _meter.CreateObservableGauge(
            "nuget.server.packages",
            () => packages switch
            {
                InMemoryPackageStore memory => memory.Count,
                DurablePackageStore durable => durable.Count,
                _ => 0
            },
            description: "Current package count.");
    }

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public long FailedRequestCount => Interlocked.Read(ref _failedRequestCount);

    public long PublishedPackageCount => Interlocked.Read(ref _publishedPackageCount);

    public long StorageFailureCount => Interlocked.Read(ref _storageFailureCount);

    public Activity? StartRequest(HttpContext context)
    {
        var activity = _activities.StartActivity("nuget.request", ActivityKind.Server);
        activity?.SetTag("http.request.method", context.Request.Method);
        activity?.SetTag("url.path", context.Request.Path.Value);
        return activity;
    }

    public void RecordRequest(HttpContext context, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "http.request.method", context.Request.Method },
            { "http.response.status_code", context.Response.StatusCode }
        };
        _requests.Add(1, tags);
        Interlocked.Increment(ref _requestCount);
        _requestDuration.Record(duration.TotalMilliseconds, tags);
        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _errors.Add(1, tags);
            Interlocked.Increment(ref _failedRequestCount);
        }
    }

    public void RecordException(HttpContext context)
    {
        _errors.Add(
            1,
            new KeyValuePair<string, object?>("http.request.method", context.Request.Method));
    }

    internal void RecordOperation(string operationId, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "nuget.operation.id", operationId },
            { "nuget.operation.outcome", outcome }
        };
        _operations.Add(1, tags);
        _operationDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordPackagePublished()
    {
        _packagesPublished.Add(1);
        Interlocked.Increment(ref _publishedPackageCount);
    }

    public void RecordStorageFailure()
    {
        _storageFailures.Add(1);
        Interlocked.Increment(ref _storageFailureCount);
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
