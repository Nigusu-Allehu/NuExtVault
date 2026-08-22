using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Extensions;
using NuGet.TestServer.Extensions.Control;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.UnitTests;

public sealed class ControlExtensionTests
{
    [Fact]
    public void Official_control_extension_owns_control_operations()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var composition = host.Services.GetRequiredService<OfficialExtensionComposition>();

        Assert.IsType<ControlExtension>(composition.Control);
        Assert.All(
            host.Registry.Registrations.Where(registration =>
                registration.Id.Value.StartsWith("NuTest.Control.", StringComparison.Ordinal)),
            registration => Assert.Equal(BuiltInExtensionIds.TestControl, registration.ExtensionId));
    }

    [Fact]
    public void Control_owner_receives_only_narrow_control_capabilities()
    {
        var constructor = Assert.Single(typeof(ControlOperations).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.Equal(
            [typeof(IPackageControlCapability), typeof(IKernelInstrumentationControlCapability)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IServiceProvider) ||
                         parameter.ParameterType == typeof(IPackageStore) ||
                         parameter.ParameterType == typeof(WebApplication) ||
                         parameter.ParameterType == typeof(IEndpointRouteBuilder));
    }

    [Fact]
    public void Programmatic_package_control_uses_the_same_scoped_capability()
    {
        var constructor = Assert.Single(
            typeof(PackageControlClient).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            candidate => candidate.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(IPackageFixtureCapability)]));

        Assert.Equal(
            [typeof(IPackageFixtureCapability)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Control_extension_never_receives_the_http_application()
    {
        Assert.DoesNotContain(
            typeof(ControlExtension).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(WebApplication) ||
                parameter.ParameterType == typeof(IEndpointRouteBuilder)));
    }
}
