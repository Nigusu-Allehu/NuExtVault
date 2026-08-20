using NuGet.TestServer.Extensions;

namespace NuGet.TestServer.Hosting.Endpoints;

internal static class OfficialExtensionEndpointBindings
{
    public static void Map(WebApplication app, OfficialExtensionComposition extensions)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(extensions);
        var graph = app.Services.GetRequiredService<ResolvedExtensionGraph>();
        if (graph.Extensions.Any(extension =>
                extension.Id == BuiltInExtensionIds.TestControl))
        {
            ControlEndpoints.Map(app);
        }
    }
}
