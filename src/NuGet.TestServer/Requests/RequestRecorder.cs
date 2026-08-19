using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Requests;

public sealed class RequestRecorder(
    TimeProvider timeProvider,
    RuntimeStateConfiguration configuration)
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, RequestRecord> _requests = [];
    private long _evictedCount;
    private long _minimumSequence;
    private long _sequence;

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    public int Capacity { get; } = configuration.RequestHistoryCapacity;
    public long EvictedCount => Interlocked.Read(ref _evictedCount);

    public RequestRecorder(TimeProvider timeProvider)
        : this(timeProvider, new RuntimeStateConfiguration())
    {
    }

    public void Add(RequestRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (request.Sequence < _minimumSequence)
            {
                return;
            }

            _requests.Add(request.Sequence, request);
            if (_requests.Count > Capacity)
            {
                _requests.Remove(_requests.First().Key);
                Interlocked.Increment(ref _evictedCount);
            }
        }
    }

    public IReadOnlyList<RequestRecord> GetAll()
    {
        lock (_gate)
        {
            return _requests.Values.ToArray();
        }
    }

    public long NextSequence()
    {
        lock (_gate)
        {
            return ++_sequence;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _minimumSequence = _sequence + 1;
            _requests.Clear();
            Interlocked.Exchange(ref _evictedCount, 0);
        }
    }
}
