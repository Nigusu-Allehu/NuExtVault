using System.Net.Http.Headers;
using System.Security.Claims;

namespace NuGet.TestServer.Authentication;

public sealed class NuGetAuthenticationMiddleware(
    RequestDelegate next,
    AuthenticationConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requirement = context.GetEndpoint()?
            .Metadata.GetMetadata<NuGetAccessRequirement>();
        if (requirement is null ||
            requirement.Kind == NuGetAccessKind.Anonymous ||
            configuration.Profile == AuthenticationProfile.Anonymous)
        {
            await next(context);
            return;
        }

        string? username = null;
        var requiresBasic = configuration.RequiresBasicAuthentication;
        var requiresApiKey =
            requirement.Kind is NuGetAccessKind.Write or NuGetAccessKind.Control &&
            configuration.RequiresApiKeyForWrites;

        if (requiresBasic)
        {
            AuthenticationHeaderValue? authorization = null;
            if (AuthenticationHeaderValue.TryParse(
                    context.Request.Headers.Authorization,
                    out var parsed))
            {
                authorization = parsed;
            }

            if (!configuration.TryAuthenticateBasic(authorization, out username))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate =
                    "Basic realm=\"NuGet Test Server\", charset=\"UTF-8\"";
                return;
            }
        }

        if (requiresApiKey &&
            !configuration.IsValidApiKey(context.Request.Headers["X-NuGet-ApiKey"].FirstOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (username is not null)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, username)],
                    authenticationType: "Basic"));
        }

        await next(context);
    }
}
