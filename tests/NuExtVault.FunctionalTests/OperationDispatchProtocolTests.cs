using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NuExtVault.Hosting;
using NuExtVault.Packages;

namespace NuExtVault.FunctionalTests;

public sealed class OperationDispatchProtocolTests
{
    [Fact]
    public async Task Package_content_is_streamed_with_ranges_and_body_free_head_requests()
    {
        await using var server = await NuExtVaultHost.StartAsync();
        var package = TestPackageBuilder.Create("Streamed.Package", "1.0.0")
            .WithFile("lib/net10.0/payload.bin", new string('a', 2 * 1024 * 1024))
            .Build();
        await server.Packages.AddAsync(package);
        const string path =
            "/flatcontainer/streamed.package/1.0.0/streamed.package.1.0.0.nupkg";

        using var streamed = await server.HttpClient.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead);
        await using var body = await streamed.Content.ReadAsStreamAsync();
        var firstChunk = new byte[64];
        var read = await body.ReadAtLeastAsync(firstChunk, 1, throwOnEndOfStream: false);
        using var head = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, path));
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 7);
        using var range = await server.HttpClient.SendAsync(rangeRequest);

        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Equal(package.Content.Length, streamed.Content.Headers.ContentLength);
        Assert.True(read > 0);
        Assert.Equal(package.Content[..read], firstChunk[..read]);
        Assert.Equal(package.Content.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(package.Content[..8], await range.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Cancelled_downloads_do_not_break_later_requests()
    {
        await using var server = await NuExtVaultHost.StartAsync();
        var package = TestPackageBuilder.Create("Cancelled.Package", "1.0.0")
            .WithFile("lib/net10.0/payload.bin", new string('b', 4 * 1024 * 1024))
            .Build();
        await server.Packages.AddAsync(package);
        const string path =
            "/flatcontainer/cancelled.package/1.0.0/cancelled.package.1.0.0.nupkg";

        using (var cancellation = new CancellationTokenSource())
        {
            using var response = await server.HttpClient.GetAsync(
                path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            await using var body = await response.Content.ReadAsStreamAsync(cancellation.Token);
            _ = await body.ReadAsync(new byte[16], cancellation.Token);
            await cancellation.CancelAsync();
        }

        using var afterCancellation = await server.HttpClient.GetAsync(path);
        using var versions = await server.HttpClient.GetAsync(
            "/flatcontainer/cancelled.package/index.json");

        Assert.Equal(HttpStatusCode.OK, afterCancellation.StatusCode);
        Assert.Equal(package.Content, await afterCancellation.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
    }

    [Fact]
    public async Task Symbol_pushes_round_trip_through_the_registry()
    {
        await using var server = await NuExtVaultHost.StartAsync();
        var package = TestPackageBuilder.Create("Symbols.Registry", "1.0.0").Build();
        var symbols = TestPackageBuilder.Create("Symbols.Registry", "1.0.0")
            .WithFile("lib/net10.0/Symbols.Registry.pdb", new string('c', 512 * 1024))
            .Build();
        await server.Packages.AddAsync(package);

        using var push = await server.HttpClient.PutAsync(
            "/symbolpackage",
            new ByteArrayContent(symbols.Content));

        Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        Assert.Equal(
            symbols.Content,
            await server.Packages.FindSymbolAsync("Symbols.Registry", "1.0.0"));
    }

    [Fact]
    public async Task Parallel_hosts_dispatch_operations_in_isolation()
    {
        await using var first = await NuExtVaultHost.StartAsync();
        await using var second = await NuExtVaultHost.StartAsync();
        await first.Packages.AddAsync(
            TestPackageBuilder.Create("Isolated.Package", "1.0.0").Build());

        using var firstVersions = await first.HttpClient.GetAsync(
            "/flatcontainer/isolated.package/index.json");
        using var secondVersions = await second.HttpClient.GetAsync(
            "/flatcontainer/isolated.package/index.json");
        using var firstState = await first.HttpClient.GetAsync("/__test/state");
        using var secondState = await second.HttpClient.GetAsync("/__test/state");

        Assert.Equal(HttpStatusCode.OK, firstVersions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, secondVersions.StatusCode);
        Assert.Equal(
            1,
            JsonDocument.Parse(await firstState.Content.ReadAsStringAsync())
                .RootElement.GetProperty("packageCount").GetInt32());
        Assert.Equal(
            0,
            JsonDocument.Parse(await secondState.Content.ReadAsStringAsync())
                .RootElement.GetProperty("packageCount").GetInt32());
    }

    [Fact]
    public async Task Control_metadata_updates_preserve_null_deprecation_messages()
    {
        await using var server = await NuExtVaultHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Deprecated.Package", "1.0.0").Build());

        using var update = await server.HttpClient.PutAsync(
            "/__test/packages/Deprecated.Package/1.0.0/metadata",
            JsonContent.Create(new
            {
                owners = new[] { "owner" },
                downloads = 3L,
                verified = false,
                deprecation = new
                {
                    reasons = new[] { "Legacy" },
                    message = (string?)null,
                    alternatePackage = (object?)null
                }
            }));
        using var leaf = await server.HttpClient.GetAsync(
            "/registration/deprecated.package/1.0.0.json");
        using var document = JsonDocument.Parse(await leaf.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement
                .GetProperty("catalogEntry")
                .GetProperty("deprecation")
                .GetProperty("message")
                .ValueKind);
    }

    [Fact]
    public async Task Typed_errors_keep_their_current_status_codes_and_bodies()
    {
        await using var server = await NuExtVaultHost.StartAsync();
        var package = TestPackageBuilder.Create("Errors.Package", "1.0.0").Build();
        await server.Packages.AddAsync(package);

        using var missing = await server.HttpClient.GetAsync(
            "/flatcontainer/errors.package/9.9.9/errors.package.9.9.9.nupkg");
        using var duplicate = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));
        using var invalid = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent([1, 2, 3, 4]));
        using var missingModerationReason = await server.HttpClient.PostAsync(
            "/__admin/packages/Errors.Package/1.0.0/approve",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(await missing.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(
            "application/problem+json",
            invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, missingModerationReason.StatusCode);
        Assert.Equal(
            "\"A moderation reason is required.\"",
            await missingModerationReason.Content.ReadAsStringAsync());
    }
}
