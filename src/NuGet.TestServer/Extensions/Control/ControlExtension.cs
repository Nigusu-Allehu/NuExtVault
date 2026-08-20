using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Extensions.Control;

internal sealed class ControlExtension
{
    public void RegisterOperations(
        OperationRegistryBuilder builder,
        IPackageControlCapability packages,
        IKernelInstrumentationControlCapability instrumentation)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(instrumentation);
        new ControlOperations(packages, instrumentation).Register(builder);
    }
}
