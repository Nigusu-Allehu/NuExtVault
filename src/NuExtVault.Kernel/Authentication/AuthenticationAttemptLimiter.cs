namespace NuExtVault.Authentication;

public sealed class AuthenticationAttemptLimiter(
    int maximumFailures,
    TimeSpan window,
    TimeProvider timeProvider)
{
    private const int MaximumTrackedClients = 10_000;
    private readonly Dictionary<string, FailureWindow> _clients =
        new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public bool TryBeginAttempt(string client, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (_clients.TryGetValue(client, out var current) &&
                now - current.StartedAt >= window)
            {
                _clients.Remove(client);
                current = null;
            }

            if (current is not null &&
                current.Failures + current.InFlight >= maximumFailures)
            {
                retryAfter = window - (now - current.StartedAt);
                return false;
            }

            if (current is null)
            {
                EnsureCapacity(now);
                current = new FailureWindow(now, 0, 0);
            }

            _clients[client] = current with { InFlight = current.InFlight + 1 };
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void CompleteAttempt(string client, bool succeeded)
    {
        lock (_lock)
        {
            if (!_clients.TryGetValue(client, out var current))
            {
                return;
            }

            var updated = current with
            {
                Failures = current.Failures + (succeeded ? 0 : 1),
                InFlight = Math.Max(0, current.InFlight - 1)
            };
            if (updated.Failures == 0 && updated.InFlight == 0)
            {
                _clients.Remove(client);
            }
            else
            {
                _clients[client] = updated;
            }
        }
    }

    private void EnsureCapacity(DateTimeOffset now)
    {
        if (_clients.Count < MaximumTrackedClients)
        {
            return;
        }

        foreach (var expired in _clients
                     .Where(pair => now - pair.Value.StartedAt >= window)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _clients.Remove(expired);
        }

        if (_clients.Count >= MaximumTrackedClients)
        {
            var oldest = _clients.MinBy(pair => pair.Value.StartedAt);
            _clients.Remove(oldest.Key);
        }
    }

    private sealed record FailureWindow(
        DateTimeOffset StartedAt,
        int Failures,
        int InFlight);
}
