using Microsoft.AspNetCore.Http;
using NuExtVault.Hosting;
using NuExtVault.Kernel;

namespace NuExtVault.UnitTests;

public sealed class KernelRequestInstrumentationTests
{
    [Fact]
    public async Task Captured_headers_are_bounded_and_sensitive_values_are_redacted()
    {
        var configuration = new RuntimeStateConfiguration(
            RuntimeStateConfiguration.DefaultRequestHistoryCapacity,
            RuntimeStateConfiguration.DefaultFaultRuleCapacity,
            sensitiveHeaders: ["X-Deployment-Secret"]);
        var instrumentation = new KernelRequestInstrumentation(
            ServerProfiles.Embedded,
            TimeProvider.System,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v3/index.json";
        context.Request.Headers.Authorization = "Basic credential";
        context.Request.Headers.Cookie = "session=credential";
        context.Request.Headers["Proxy-Authorization"] = "Basic proxy-credential";
        context.Request.Headers["X-NuGet-ApiKey"] = "api-key";
        context.Request.Headers["X-Api-Key"] = "alternate-api-key";
        context.Request.Headers["X-Custom-API_KEY"] = "custom-api-key";
        context.Request.Headers["Cookie2"] = "legacy-cookie";
        context.Request.Headers["X-Deployment-Secret"] = "deployment-secret";
        context.Request.Headers["X-Correlation-Id"] = "safe-value";
        context.Request.Headers["X-Long"] = new string('x', 2048);
        for (var index = 0; index < 70; index++)
        {
            context.Request.Headers[$"X-Bounded-{index:D2}"] = index.ToString();
        }

        await instrumentation.InvokeAsync(
            context,
            _ =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });

        var captured = Assert.Single(instrumentation.GetCapturedRequests());
        Assert.Equal("[REDACTED]", captured.Headers["Authorization"]);
        Assert.Equal("[REDACTED]", captured.Headers["Cookie"]);
        Assert.Equal("[REDACTED]", captured.Headers["Proxy-Authorization"]);
        Assert.Equal("[REDACTED]", captured.Headers["X-NuGet-ApiKey"]);
        Assert.Equal("[REDACTED]", captured.Headers["X-Api-Key"]);
        Assert.Equal("[REDACTED]", captured.Headers["X-Custom-API_KEY"]);
        Assert.Equal("[REDACTED]", captured.Headers["Cookie2"]);
        Assert.Equal("[REDACTED]", captured.Headers["X-Deployment-Secret"]);
        Assert.True(captured.Headers.Count <= 64);
        Assert.All(captured.Headers.Values, value => Assert.True(value.Length <= 1024));
        Assert.False(captured.BodyStored);
    }

    [Fact]
    public async Task Production_profile_does_not_capture_requests()
    {
        var instrumentation = new KernelRequestInstrumentation(
            ServerProfiles.Production,
            TimeProvider.System,
            new RuntimeStateConfiguration());
        var context = new DefaultHttpContext();
        context.Request.Path = "/v3/index.json";

        await instrumentation.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Empty(instrumentation.GetRequests());
    }
}
