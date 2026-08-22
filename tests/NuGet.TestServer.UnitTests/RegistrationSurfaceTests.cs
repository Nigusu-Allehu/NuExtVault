using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Extensions.Registration;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.UnitTests;

public sealed class RegistrationSurfaceTests
{
    [Fact]
    public void Registration_surface_is_complete_and_body_free()
    {
        Assert.Equal(
            ["registration.index", "registration.page", "registration.leaf"],
            RegistrationEndpoints.All.Select(endpoint => endpoint.Name));
        Assert.Equal(
            [
                OperationIds.RegistrationGetIndex,
                OperationIds.RegistrationGetPage,
                OperationIds.RegistrationGetLeaf
            ],
            RegistrationEndpoints.All.SelectMany(endpoint =>
                endpoint.Operations.Select(operation => operation.OperationId)));
        Assert.All(
            RegistrationEndpoints.All,
            endpoint =>
            {
                Assert.Equal(["GET", "HEAD"], endpoint.Methods.ToArray());
                Assert.Equal(EndpointHeadPolicy.MirrorsGet, endpoint.Head);
                Assert.Equal(EndpointBodyBinding.None, endpoint.Body);
                Assert.Equal(EndpointLimits.BodyFree, endpoint.Limits);
            });
    }

    [Fact]
    public void Registration_owner_depends_only_on_narrow_capabilities_and_contributions()
    {
        var constructor = Assert.Single(typeof(RegistrationOperations).GetConstructors());

        Assert.Equal(
            [
                typeof(IRegistrationMetadataReadCapability),
                typeof(IRegistrationVulnerabilityReadCapability),
                typeof(IDocumentContributionSource)
            ],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
