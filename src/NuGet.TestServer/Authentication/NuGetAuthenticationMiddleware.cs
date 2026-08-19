using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Authentication;

public sealed class NuGetAuthenticationMiddleware(
    RequestDelegate next,
    AuthenticationConfiguration configuration,
    ServerHostingOptions hosting,
    AuthenticationAttemptLimiter limiter,
    ISecurityAuditSink audits)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requirement = context.GetEndpoint()?
            .Metadata.GetMetadata<NuGetAccessRequirement>();
        if (configuration.Profile == AuthenticationProfile.Production)
        {
            if (!IsSecureTransport(context, hosting.Transport))
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            if (!context.Request.IsHttps)
            {
                context.Request.Scheme = Uri.UriSchemeHttps;
            }

            if (requirement is null || requirement.Kind == NuGetAccessKind.Anonymous)
            {
                await next(context);
                return;
            }

            await AuthenticateProductionAsync(context, requirement);
            return;
        }

        if (requirement is null || requirement.Kind == NuGetAccessKind.Anonymous ||
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

    private async Task AuthenticateProductionAsync(
        HttpContext context,
        NuGetAccessRequirement requirement)
    {
        var client = GetClient(context, hosting.Transport);
        context.Items[SecurityContextItems.Client] = client;
        if (!limiter.TryBeginAttempt(client, out var retryAfter))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            Audit(context, SecurityAuditEventType.AuthenticationThrottled, client, null, null);
            return;
        }

        var security = configuration.ProductionSecurity!;
        ProductionIdentity? identity = null;
        var apiKey = context.Request.Headers["X-NuGet-ApiKey"].FirstOrDefault();
        var authenticated = security.TryAuthenticateApiKey(apiKey, out identity);
        if (!authenticated &&
            TryReadBasicCredentials(context.Request.Headers.Authorization, out var username, out var password))
        {
            authenticated = security.TryAuthenticateBasic(username, password, out identity);
        }

        if (!authenticated)
        {
            limiter.CompleteAttempt(client, succeeded: false);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate =
                "Basic realm=\"NuGet Test Server\", charset=\"UTF-8\"";
            Audit(context, SecurityAuditEventType.AuthenticationFailed, client, null, null);
            return;
        }

        limiter.CompleteAttempt(client, succeeded: true);
        var requiredScope = requirement.Kind switch
        {
            NuGetAccessKind.Read => SecurityScope.Read,
            NuGetAccessKind.Publish or NuGetAccessKind.Write => SecurityScope.Publish,
            NuGetAccessKind.Unlist => SecurityScope.Unlist,
            NuGetAccessKind.Delete => SecurityScope.Delete,
            NuGetAccessKind.Admin or NuGetAccessKind.Control => SecurityScope.Admin,
            _ => SecurityScope.Read
        };
        if (!identity!.HasScope(requiredScope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            Audit(
                context,
                SecurityAuditEventType.AuthorizationDenied,
                client,
                identity.Name,
                $"Missing scope '{requiredScope}'.");
            return;
        }

        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, identity.Name)],
                authenticationType: "Production"));
        context.Items[typeof(ProductionIdentity)] = identity;
        Audit(
            context,
            SecurityAuditEventType.AuthenticationSucceeded,
            client,
            identity.Name,
            null);
        await next(context);
    }

    private static bool IsSecureTransport(
        HttpContext context,
        TransportSecurityOptions transport)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        return transport.IsTrustedProxy(context.Connection.RemoteIpAddress) &&
               context.Request.Headers["X-Forwarded-Proto"].Count == 1 &&
               string.Equals(
                   context.Request.Headers["X-Forwarded-Proto"][0],
                   Uri.UriSchemeHttps,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string GetClient(
        HttpContext context,
        TransportSecurityOptions transport)
    {
        if (transport.IsTrustedProxy(context.Connection.RemoteIpAddress) &&
            context.Request.Headers["X-Forwarded-For"].Count == 1)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"][0];
            if (System.Net.IPAddress.TryParse(forwarded, out var address))
            {
                return address.ToString();
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static bool TryReadBasicCredentials(
        string? authorizationValue,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;
        if (!AuthenticationHeaderValue.TryParse(authorizationValue, out var authorization) ||
            !authorization.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return false;
        }

        try
        {
            var value = new UTF8Encoding(false, true).GetString(
                Convert.FromBase64String(authorization.Parameter));
            var separator = value.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            username = value[..separator];
            password = value[(separator + 1)..];
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private void Audit(
        HttpContext context,
        SecurityAuditEventType eventType,
        string client,
        string? identity,
        string? detail)
    {
        audits.Write(new SecurityAuditEvent(
            DateTimeOffset.UtcNow,
            eventType,
            client,
            identity,
            context.Request.Method,
            context.Request.Path,
            detail));
    }
}
