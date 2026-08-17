using System.Collections.Concurrent;

namespace NuGet.TestServer.Requests;

public sealed class RequestRecorder(TimeProvider timeProvider)
{
    private readonly ConcurrentQueue<RequestRecord> _requests = [];
    private long _sequence;

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public void Add(RequestRecord request) => _requests.Enqueue(request);

    public IReadOnlyList<RequestRecord> GetAll() =>
        _requests.OrderBy(request => request.Sequence).ToArray();

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Reset()
    {
        _requests.Clear();
        Interlocked.Exchange(ref _sequence, 0);
    }
}
