using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Hosting.Endpoints;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Owners.Registration;

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
    public void Registration_owner_depends_only_on_its_query_and_document_builder()
    {
        var constructor = Assert.Single(typeof(RegistrationOperations).GetConstructors());

        Assert.Equal(
            [typeof(IRegistrationPackageQuery), typeof(RegistrationDocumentBuilder)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
