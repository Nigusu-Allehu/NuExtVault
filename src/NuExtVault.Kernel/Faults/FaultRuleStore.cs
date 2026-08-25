using NuExtVault.Hosting;

namespace NuExtVault.Faults;

public sealed class FaultRuleStore(RuntimeStateConfiguration configuration)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RuleState> _rules =
        new(StringComparer.OrdinalIgnoreCase);

    public int Capacity { get; } = configuration.FaultRuleCapacity;

    public FaultRuleStore()
        : this(new RuntimeStateConfiguration())
    {
    }

    public void Add(FaultRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            throw new ArgumentException("A fault rule ID is required.", nameof(rule));
        }

        if (rule.RemainingMatches < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), "Remaining matches must be positive.");
        }

        lock (_gate)
        {
            if (_rules.ContainsKey(rule.Id))
            {
                throw new FaultRuleConflictException(
                    $"Fault rule '{rule.Id}' already exists.");
            }

            if (_rules.Count >= Capacity)
            {
                throw new FaultRuleConflictException(
                    $"The fault rule capacity of {Capacity} has been reached.");
            }

            _rules.Add(rule.Id, new RuleState(rule));
        }
    }

    internal sealed class FaultRuleConflictException(string message)
        : InvalidOperationException(message);

    public FaultRule? Match(string method, string path)
    {
        lock (_gate)
        {
            foreach (var state in _rules.Values.OrderBy(
                         value => value.Rule.Id,
                         StringComparer.Ordinal))
            {
                if (state.TryMatch(method, path))
                {
                    return state.Rule;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<FaultRule> GetAll()
    {
        lock (_gate)
        {
            return _rules.Values
                .Select(state => state.Snapshot())
                .OrderBy(rule => rule.Id)
                .ToArray();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    private sealed class RuleState(FaultRule rule)
    {
        private int _remaining = rule.RemainingMatches;
        public FaultRule Rule { get; } = rule;

        public bool TryMatch(string method, string path)
        {
            if (Rule.Method is not null &&
                !string.Equals(Rule.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Rule.PathContains is not null &&
                !path.Contains(Rule.PathContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (remaining <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining)
                {
                    return true;
                }
            }
        }

        public FaultRule Snapshot() => Rule with { RemainingMatches = Math.Max(0, _remaining) };
    }
}
