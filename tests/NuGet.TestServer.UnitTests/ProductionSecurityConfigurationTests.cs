using NuGet.TestServer.Authentication;

namespace NuGet.TestServer.UnitTests;

public sealed class ProductionSecurityConfigurationTests
{
    [Fact]
    public void Rotated_credentials_authenticate_the_same_scoped_identity()
    {
        var security = ProductionSecurityConfiguration.Create(
        [
            new ProductionIdentityOptions(
                "publisher",
                ["old-key", "new-key"],
                [SecurityScope.Read, SecurityScope.Publish],
                ["Contoso."])
        ]);

        Assert.True(security.TryAuthenticateApiKey("old-key", out var oldIdentity));
        Assert.True(security.TryAuthenticateApiKey("new-key", out var newIdentity));
        Assert.Same(oldIdentity, newIdentity);
        Assert.True(oldIdentity!.HasScope(SecurityScope.Publish));
        Assert.False(oldIdentity.HasScope(SecurityScope.Delete));
        Assert.True(oldIdentity.AllowsPackage("Contoso.Logging"));
        Assert.False(oldIdentity.AllowsPackage("Other.Logging"));
    }

    [Fact]
    public void Duplicate_identity_names_and_credentials_are_rejected()
    {
        Assert.Throws<AuthenticationConfigurationException>(() =>
            ProductionSecurityConfiguration.Create(
            [
                new("publisher", ["shared"], [SecurityScope.Publish], ["A."]),
                new("publisher", ["other"], [SecurityScope.Publish], ["B."])
            ]));

        Assert.Throws<AuthenticationConfigurationException>(() =>
            ProductionSecurityConfiguration.Create(
            [
                new("first", ["shared"], [SecurityScope.Publish], ["A."]),
                new("second", ["shared"], [SecurityScope.Publish], ["B."])
            ]));
    }

    [Fact]
    public void Concurrent_failed_authentication_is_bounded_per_client()
    {
        var limiter = new AuthenticationAttemptLimiter(
            maximumFailures: 3,
            window: TimeSpan.FromMinutes(1),
            TimeProvider.System);

        var accepted = 0;
        Parallel.For(0, 20, iteration =>
        {
            if (limiter.TryBeginAttempt("192.0.2.10", out _))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.Equal(3, accepted);
        Assert.False(limiter.TryBeginAttempt("192.0.2.10", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(limiter.TryBeginAttempt("192.0.2.11", out _));
    }

    [Fact]
    public void Security_audit_memory_retention_is_bounded()
    {
        var audits = new SecurityAuditSink(storageDirectory: null);
        for (var index = 0; index < 1_100; index++)
        {
            audits.Write(new SecurityAuditEvent(
                DateTimeOffset.UtcNow,
                SecurityAuditEventType.AuthenticationFailed,
                "192.0.2.10",
                null,
                "GET",
                "/v3/index.json",
                null));
        }

        Assert.Equal(1_000, audits.GetAll().Count);
    }
}
