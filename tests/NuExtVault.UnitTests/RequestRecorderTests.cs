using NuExtVault.Hosting;
using NuExtVault.Requests;

namespace NuExtVault.UnitTests;

public sealed class RequestRecorderTests
{
    [Fact]
    public void Recorder_evicts_the_oldest_sequences_regardless_of_completion_order()
    {
        var recorder = CreateRecorder(capacity: 3);

        foreach (var sequence in new long[] { 5, 1, 4, 2, 3 })
        {
            recorder.Add(CreateRecord(sequence));
        }

        Assert.Equal([3L, 4L, 5L], recorder.GetAll().Select(request => request.Sequence));
        Assert.Equal(2, recorder.EvictedCount);
    }

    [Fact]
    public void Recorder_remains_bounded_and_ordered_under_concurrent_writes()
    {
        const int capacity = 64;
        const int requestCount = 1_000;
        var recorder = CreateRecorder(capacity);

        Parallel.For(0, requestCount, _ =>
        {
            var sequence = recorder.NextSequence();
            recorder.Add(CreateRecord(sequence));
        });

        var requests = recorder.GetAll();
        Assert.Equal(capacity, requests.Count);
        Assert.Equal(
            Enumerable.Range(requestCount - capacity + 1, capacity).Select(value => (long)value),
            requests.Select(request => request.Sequence));
        Assert.Equal(requestCount - capacity, recorder.EvictedCount);
    }

    [Fact]
    public void Reset_clears_requests_and_eviction_count()
    {
        var recorder = CreateRecorder(capacity: 1);
        recorder.Add(CreateRecord(recorder.NextSequence()));
        recorder.Add(CreateRecord(recorder.NextSequence()));

        recorder.Reset();

        Assert.Empty(recorder.GetAll());
        Assert.Equal(0, recorder.EvictedCount);
    }

    [Fact]
    public void Captured_headers_do_not_change_public_request_record_equality()
    {
        var first = CreateRecord(1);
        var second = CreateRecord(1);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    private static RequestRecorder CreateRecorder(int capacity) =>
        new(
            TimeProvider.System,
            new RuntimeStateConfiguration(
                requestHistoryCapacity: capacity,
                faultRuleCapacity: RuntimeStateConfiguration.DefaultFaultRuleCapacity));

    private static RequestRecord CreateRecord(long sequence) =>
        new(
            sequence,
            DateTimeOffset.UnixEpoch,
            "GET",
            $"/{sequence}",
            200,
            0,
            null,
            null);
}
