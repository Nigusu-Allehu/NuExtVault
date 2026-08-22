namespace NuGet.TestServer.Hosting;

public sealed class RuntimeStateConfiguration
{
    public const int DefaultRequestHistoryCapacity = 10_000;
    public const int DefaultFaultRuleCapacity = 100;

    public RuntimeStateConfiguration(
        int requestHistoryCapacity = DefaultRequestHistoryCapacity,
        int faultRuleCapacity = DefaultFaultRuleCapacity)
        : this(requestHistoryCapacity, faultRuleCapacity, null)
    {
    }

    public RuntimeStateConfiguration(
        int requestHistoryCapacity,
        int faultRuleCapacity,
        IEnumerable<string>? sensitiveHeaders)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestHistoryCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(faultRuleCapacity, 1);

        RequestHistoryCapacity = requestHistoryCapacity;
        FaultRuleCapacity = faultRuleCapacity;
        SensitiveHeaders = (sensitiveHeaders ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public int RequestHistoryCapacity { get; }
    public int FaultRuleCapacity { get; }
    internal IReadOnlySet<string> SensitiveHeaders { get; }

    internal static RuntimeStateConfiguration FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("RuntimeState");
        return new RuntimeStateConfiguration(
            section.GetValue("RequestHistoryCapacity", DefaultRequestHistoryCapacity),
            section.GetValue("FaultRuleCapacity", DefaultFaultRuleCapacity),
            section.GetSection("SensitiveHeaders").Get<string[]>());
    }
}
