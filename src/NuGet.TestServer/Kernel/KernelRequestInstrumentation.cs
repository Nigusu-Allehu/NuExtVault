using System.Diagnostics;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Requests;

namespace NuGet.TestServer.Kernel;

internal sealed class KernelRequestInstrumentation
{
    private const string RedactedValue = "[REDACTED]";
    private const int MaximumCapturedHeaders = 64;
    private const int MaximumHeaderValueLength = 1024;
    private static readonly HashSet<string> AlwaysSensitiveHeaders = new(
        [
            "Authorization",
            "Cookie",
            "Proxy-Authorization",
            "Set-Cookie",
            "X-Api-Key",
            "X-NuGet-ApiKey"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly FaultRuleStore _faults;
    private readonly RequestRecorder _requests;
    private readonly IReadOnlySet<string> _configuredSensitiveHeaders;
    private readonly object _activeGate = new();
    private readonly SortedDictionary<long, TaskCompletionSource> _activeRequests = [];
    private readonly AsyncLocal<long?> _currentSequence = new();

    public KernelRequestInstrumentation(
        ServerProfile profile,
        TimeProvider timeProvider,
        RuntimeStateConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(configuration);
        IsEnabled = profile.Grants.Any(
            grant => grant.Name == BuiltInCapabilityNames.ControlFaultsInject) &&
            profile.Grants.Any(
                grant => grant.Name == BuiltInCapabilityNames.ControlRequestsRead);
        _faults = new FaultRuleStore(configuration);
        _requests = new RequestRecorder(timeProvider, configuration);
        _configuredSensitiveHeaders = configuration.SensitiveHeaders;
    }

    public bool IsEnabled { get; }
    public int FaultCapacity => _faults.Capacity;
    public int RequestCapacity => _requests.Capacity;
    public long EvictedRequestCount => _requests.EvictedCount;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (!IsEnabled)
        {
            await next(context);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long sequence;
        lock (_activeGate)
        {
            sequence = _requests.NextSequence();
            _activeRequests.Add(sequence, completion);
        }

        var previousSequence = _currentSequence.Value;
        _currentSequence.Value = sequence;
        var started = Stopwatch.GetTimestamp();
        string? faultRuleId = null;
        try
        {
            var fault = context.Request.Path.StartsWithSegments("/__test")
                ? null
                : _faults.Match(context.Request.Method, context.Request.Path);
            if (fault is not null)
            {
                faultRuleId = fault.Id;
                if (fault.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(fault.Delay, context.RequestAborted);
                }

                context.Response.StatusCode = (int)fault.StatusCode;
                return;
            }

            await next(context);
        }
        finally
        {
            try
            {
                if (!ClearsRequestHistory(context.Request))
                {
                    _requests.Add(new CapturedRequestRecord(
                        new RequestRecord(
                            sequence,
                            _requests.UtcNow,
                            context.Request.Method,
                            context.Request.Path,
                            context.Response.StatusCode,
                            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            faultRuleId,
                            context.User.Identity?.Name),
                        CaptureHeaders(context.Request.Headers)));
                }
            }
            finally
            {
                _currentSequence.Value = previousSequence;
                lock (_activeGate)
                {
                    _activeRequests.Remove(sequence);
                }

                completion.SetResult();
            }
        }
    }

    public IReadOnlyList<FaultRule> GetFaults() => _faults.GetAll();
    public void AddFault(FaultRule rule) => _faults.Add(rule);
    public void ClearFaults() => _faults.Reset();
    public IReadOnlyList<RequestRecord> GetRequests() => _requests.GetAll();
    internal IReadOnlyList<CapturedRequestRecord> GetCapturedRequests() => _requests.GetCaptured();
    public void ClearRequests() => _requests.Reset();

    public async ValueTask WaitForCompletedRequestsAsync(CancellationToken token)
    {
        var currentSequence = _currentSequence.Value;
        Task[] completions;
        lock (_activeGate)
        {
            completions =
            [
                .. _activeRequests
                    .Where(request => currentSequence is null ||
                                      request.Key < currentSequence)
                    .Select(request => request.Value.Task)
            ];
        }

        if (completions.Length > 0)
        {
            await Task.WhenAll(completions).WaitAsync(token);
        }
    }

    private IReadOnlyDictionary<string, string> CaptureHeaders(IHeaderDictionary headers)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers
                     .OrderByDescending(item => IsSensitive(item.Key))
                     .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(MaximumCapturedHeaders))
        {
            captured[header.Key] = IsSensitive(header.Key)
                ? RedactedValue
                : Truncate(header.Value.ToString());
        }

        return captured;
    }

    private bool IsSensitive(string name)
    {
        if (AlwaysSensitiveHeaders.Contains(name) || _configuredSensitiveHeaders.Contains(name))
        {
            return true;
        }

        var normalized = NormalizeHeaderName(name);
        return normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("cookie", StringComparison.Ordinal) ||
               normalized is "authorization" or "proxyauthorization";
    }

    private static string NormalizeHeaderName(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Truncate(string value) =>
        value.Length <= MaximumHeaderValueLength
            ? value
            : value[..MaximumHeaderValueLength];

    private static bool ClearsRequestHistory(HttpRequest request) =>
        (HttpMethods.IsPost(request.Method) &&
         request.Path.Equals("/__test/reset", StringComparison.OrdinalIgnoreCase)) ||
        (HttpMethods.IsDelete(request.Method) &&
         request.Path.Equals("/__test/requests", StringComparison.OrdinalIgnoreCase));
}
