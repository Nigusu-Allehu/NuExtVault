using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Counts every whole extension state record payload the state layer materializes on
/// the current asynchronous flow. It proves which operations are bounded by a record
/// header or a streaming buffer rather than by the size of the persisted record set.
/// </summary>
internal sealed class StatePayloadProbe : IDisposable
{
    private long _count;
    private long _bytes;

    public StatePayloadProbe() =>
        StatePayloadInstrumentation.Current = length =>
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _bytes, length);
        };

    public long Count => Interlocked.Read(ref _count);

    public long Bytes => Interlocked.Read(ref _bytes);

    public void Dispose() => StatePayloadInstrumentation.Current = null;
}
