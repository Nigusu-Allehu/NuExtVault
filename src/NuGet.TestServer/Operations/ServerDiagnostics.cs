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

    public ServerDiagnostics(InMemoryPackageStore packages)
    {
        _requests = _meter.CreateCounter<long>("nuget.server.requests");
        _errors = _meter.CreateCounter<long>("nuget.server.errors");
        _requestDuration = _meter.CreateHistogram<double>(
            "nuget.server.request.duration",
            "ms");
        _packagesPublished = _meter.CreateCounter<long>("nuget.server.packages.published");
        _storageFailures = _meter.CreateCounter<long>("nuget.server.storage.failures");
        _meter.CreateObservableGauge(
            "nuget.server.packages",
            () => packages.Count,
            description: "Current package count.");
    }

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
        _requestDuration.Record(duration.TotalMilliseconds, tags);
        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _errors.Add(1, tags);
        }
    }

    public void RecordException(HttpContext context)
    {
        _errors.Add(
            1,
            new KeyValuePair<string, object?>("http.request.method", context.Request.Method));
    }

    public void RecordPackagePublished() => _packagesPublished.Add(1);

    public void RecordStorageFailure() => _storageFailures.Add(1);

    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
