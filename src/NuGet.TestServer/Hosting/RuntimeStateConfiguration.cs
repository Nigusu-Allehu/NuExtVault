namespace NuGet.TestServer.Hosting;

public sealed class RuntimeStateConfiguration
{
    public const int DefaultRequestHistoryCapacity = 10_000;
    public const int DefaultFaultRuleCapacity = 100;

    public RuntimeStateConfiguration(
        int requestHistoryCapacity = DefaultRequestHistoryCapacity,
        int faultRuleCapacity = DefaultFaultRuleCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestHistoryCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(faultRuleCapacity, 1);

        RequestHistoryCapacity = requestHistoryCapacity;
        FaultRuleCapacity = faultRuleCapacity;
    }

    public int RequestHistoryCapacity { get; }
    public int FaultRuleCapacity { get; }

    internal static RuntimeStateConfiguration FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("RuntimeState");
        return new RuntimeStateConfiguration(
            section.GetValue("RequestHistoryCapacity", DefaultRequestHistoryCapacity),
            section.GetValue("FaultRuleCapacity", DefaultFaultRuleCapacity));
    }
}
