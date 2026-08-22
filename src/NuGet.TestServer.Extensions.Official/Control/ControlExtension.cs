using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.Control;

internal sealed class ControlExtension
{
    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IPackageControlCapability packages,
        IKernelInstrumentationControlCapability instrumentation)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(instrumentation);
        new ControlOperations(packages, instrumentation).Register(registry);
    }
}
