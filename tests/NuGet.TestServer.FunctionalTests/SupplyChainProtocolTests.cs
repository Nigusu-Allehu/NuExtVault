using System.Net;
using System.Text.Json;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class SupplyChainProtocolTests
{
    [Fact]
    public async Task Public_resources_apply_visibility_immediately_and_keep_admin_reads_separate()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Visibility.Protocol", "1.0.0")
            .WithDescription("authoritative visibility metadata")
            .WithPackageType("DotnetTool", "1.0.0")
            .Build();
        var symbols = TestPackageBuilder.Create("Visibility.Protocol", "1.0.0")
            .WithFile("lib/net10.0/Visibility.Protocol.pdb", [1, 2, 3, 4])
            .Build();
        await server.Packages.AddAsync(package);
        using var symbolPush = await server.HttpClient.PutAsync(
            "/symbolpackage",
            new ByteArrayContent(symbols.Content));
        Assert.Equal(HttpStatusCode.Created, symbolPush.StatusCode);

        using var unlist = await server.HttpClient.PostAsync(
            "/__test/packages/Visibility.Protocol/1.0.0/unlist",
            null);
        using var exact = await server.HttpClient.GetAsync(
            "/flatcontainer/visibility.protocol/1.0.0/visibility.protocol.1.0.0.nupkg");
        using var versions = await server.HttpClient.GetAsync(
            "/flatcontainer/visibility.protocol/index.json");
        using var registrationResponse = await server.HttpClient.GetAsync(
            "/registration/visibility.protocol/index.json");
        using var registration = JsonDocument.Parse(
            await registrationResponse.Content.ReadAsStreamAsync());
        using var searchResponse = await server.HttpClient.GetAsync(
            "/query?q=Visibility.Protocol&packageType=DotnetTool");
        using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.NoContent, unlist.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Contains("1.0.0", await versions.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(registration.RootElement
            .GetProperty("items")[0]
            .GetProperty("items")[0]
            .GetProperty("catalogEntry")
            .GetProperty("listed")
            .GetBoolean());
        Assert.Equal(
            "authoritative visibility metadata",
            registration.RootElement
                .GetProperty("items")[0]
                .GetProperty("items")[0]
                .GetProperty("catalogEntry")
                .GetProperty("description")
                .GetString());
        Assert.Equal(0, search.RootElement.GetProperty("totalHits").GetInt32());
        Assert.Equal(symbols.Content, await server.Packages.FindSymbolAsync(
            "Visibility.Protocol",
            "1.0.0"));

        using var quarantine = await server.HttpClient.PostAsync(
            "/__admin/packages/Visibility.Protocol/1.0.0/quarantine?reason=investigation",
            null);
        using var hiddenExact = await server.HttpClient.GetAsync(
            "/flatcontainer/visibility.protocol/1.0.0/visibility.protocol.1.0.0.nupkg");
        using var hiddenVersions = await server.HttpClient.GetAsync(
            "/flatcontainer/visibility.protocol/index.json");
        using var hiddenRegistration = await server.HttpClient.GetAsync(
            "/registration/visibility.protocol/index.json");
        using var hiddenSearchResponse = await server.HttpClient.GetAsync(
            "/query?q=Visibility.Protocol");
        using var hiddenSearch = JsonDocument.Parse(
            await hiddenSearchResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.NoContent, quarantine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenExact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenVersions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenRegistration.StatusCode);
        Assert.Equal(0, hiddenSearch.RootElement.GetProperty("totalHits").GetInt32());
        Assert.Null(await server.Packages.FindSymbolAsync("Visibility.Protocol", "1.0.0"));

        using var approve = await server.HttpClient.PostAsync(
            "/__admin/packages/Visibility.Protocol/1.0.0/approve?reason=validated",
            null);
        using var delete = await server.HttpClient.PostAsync(
            "/__admin/packages/Visibility.Protocol/1.0.0/delete?reason=retired",
            null);
        using var deletedExact = await server.HttpClient.GetAsync(
            "/flatcontainer/visibility.protocol/1.0.0/visibility.protocol.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deletedExact.StatusCode);
    }

    [Fact]
    public async Task Malicious_unsigned_quarantined_and_over_quota_packages_are_not_restorable()
    {
        var scanner = new SelectiveScanner();
        var options = new SupplyChainOptions
        {
            MaximumPackagesPerIdentity = 2,
            MaximumPackagesPerRepository = 2
        };
        await using var server = await NuGetTestServerHost.StartAsync(options, scanner);
        var malicious = TestPackageBuilder.Create("Malicious.Protocol", "1.0.0").Build();
        var clean = TestPackageBuilder.Create("Clean.Protocol", "1.0.0").Build();
        var overQuota = TestPackageBuilder.Create("Quota.Protocol", "1.0.0").Build();

        using var maliciousPush = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(malicious.Content));
        using var maliciousRestore = await server.HttpClient.GetAsync(
            "/flatcontainer/malicious.protocol/1.0.0/malicious.protocol.1.0.0.nupkg");
        using var cleanPush = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(clean.Content));
        using var overQuotaPush = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(overQuota.Content));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, maliciousPush.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, maliciousRestore.StatusCode);
        Assert.Equal(HttpStatusCode.Created, cleanPush.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, overQuotaPush.StatusCode);
    }

    [Fact]
    public async Task Duplicate_content_is_idempotent_but_changed_published_version_conflicts()
    {
        await using var server = await NuGetTestServerHost.StartAsync(
            new SupplyChainOptions(),
            new SelectiveScanner());
        var original = TestPackageBuilder.Create("Immutable.Protocol", "1.0.0").Build();
        var changed = TestPackageBuilder.Create("Immutable.Protocol", "1.0.0")
            .WithFile("changed.txt", "changed"u8.ToArray())
            .Build();

        using var first = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(original.Content));
        using var duplicate = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(original.Content));
        using var conflict = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(changed.Content));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Invalid_signature_entry_is_rejected_and_never_discoverable()
    {
        await using var server = await NuGetTestServerHost.StartAsync(
            new SupplyChainOptions(),
            new SelectiveScanner());
        var invalidlySigned = TestPackageBuilder.Create("Invalid.Signature", "1.0.0")
            .WithFile(".signature.p7s", "not a signature"u8.ToArray())
            .Build();

        using var push = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(invalidlySigned.Content));
        using var registration = await server.HttpClient.GetAsync(
            "/registration/invalid.signature/index.json");
        using var search = await server.HttpClient.GetAsync("/query?q=Invalid.Signature");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, push.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, registration.StatusCode);
        Assert.DoesNotContain(
            "Invalid.Signature",
            await search.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Required_signature_rejects_unsigned_package()
    {
        await using var server = await NuGetTestServerHost.StartAsync(
            new SupplyChainOptions { RequireSignedPackages = true },
            new SelectiveScanner());
        var unsigned = TestPackageBuilder.Create("Unsigned.Protocol", "1.0.0").Build();

        using var push = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(unsigned.Content));
        using var restore = await server.HttpClient.GetAsync(
            "/flatcontainer/unsigned.protocol/1.0.0/unsigned.protocol.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, push.StatusCode);
        Assert.Contains(
            "signature",
            await push.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
    }

    [Fact]
    public async Task Moderation_can_approve_reject_quarantine_and_controlled_delete()
    {
        await using var server = await NuGetTestServerHost.StartAsync(
            new SupplyChainOptions(),
            new SelectiveScanner());
        var package = TestPackageBuilder.Create("Malicious.Moderated", "1.0.0").Build();
        using var push = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, push.StatusCode);

        using var approve = await server.HttpClient.PostAsync(
            "/__admin/packages/Malicious.Moderated/1.0.0/approve?reason=false-positive",
            null);
        using var visible = await server.HttpClient.GetAsync(
            "/flatcontainer/malicious.moderated/1.0.0/malicious.moderated.1.0.0.nupkg");
        using var quarantine = await server.HttpClient.PostAsync(
            "/__admin/packages/Malicious.Moderated/1.0.0/quarantine?reason=investigation",
            null);
        using var hidden = await server.HttpClient.GetAsync(
            "/flatcontainer/malicious.moderated/1.0.0/malicious.moderated.1.0.0.nupkg");
        using var delete = await server.HttpClient.PostAsync(
            "/__admin/packages/Malicious.Moderated/1.0.0/delete?reason=confirmed-malware",
            null);
        using var audit = await server.HttpClient.GetAsync("/__admin/supply-chain/audit");

        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, quarantine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Contains(
            "confirmed-malware",
            await audit.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private sealed class SelectiveScanner : IPackagePolicyScanner
    {
        public ValueTask<PackageScanResult> ScanAsync(
            TestPackage package,
            CancellationToken token = default) =>
            ValueTask.FromResult(
                package.Identity.Id.StartsWith("Malicious", StringComparison.OrdinalIgnoreCase)
                    ? new PackageScanResult(PackageScanOutcome.Malicious, "deterministic test match")
                    : new PackageScanResult(PackageScanOutcome.Clean, "deterministic test clean"));
    }
}
