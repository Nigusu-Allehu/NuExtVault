using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

/// <summary>
/// Step 13 compatibility gates for the extracted flat-container owner. The URLs,
/// aliases, status codes, headers, range and HEAD behavior, normalization, integrity,
/// and read-your-writes consistency must not change when route ownership moves to the
/// official extension.
/// </summary>
public sealed class FlatContainerResourceTests
{
    [Fact]
    public async Task Flat_container_urls_preserve_status_headers_range_and_head_behavior()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Flat.Owner", "1.0.0")
            .WithFile("lib/net10.0/payload.bin", new string('c', 32 * 1024))
            .Build();
        await server.Packages.AddAsync(package);
        const string contentPath = "/flatcontainer/flat.owner/1.0.0/flat.owner.1.0.0.nupkg";

        using var versions = await server.HttpClient.GetAsync(
            "/flatcontainer/flat.owner/index.json");
        using var aliased = await server.HttpClient.GetAsync(
            "/flatcontainer/FLAT.OWNER/1.0/flat.owner.1.0.0.nupkg");
        using var download = await server.HttpClient.GetAsync(contentPath);
        using var head = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, contentPath));
        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, contentPath);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 9);
        using var ranged = await server.HttpClient.SendAsync(rangeRequest);
        using var nuspec = await server.HttpClient.GetAsync(
            "/flatcontainer/flat.owner/1.0.0/flat.owner.nuspec");
        using var hash = await server.HttpClient.GetAsync(
            "/flatcontainer/flat.owner/1.0.0/flat.owner.1.0.0.nupkg.sha512");
        using var unknown = await server.HttpClient.GetAsync(
            "/flatcontainer/flat.owner/1.0.0/flat.owner.1.0.0.unknown");
        using var missing = await server.HttpClient.GetAsync(
            "/flatcontainer/flat.missing/index.json");

        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        using var versionsJson = JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
        Assert.Equal(
            ["1.0.0"],
            versionsJson.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(version => version.GetString()));

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(package.ContentLength, download.Content.Headers.ContentLength);
        Assert.Equal(package.Content, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, aliased.StatusCode);
        Assert.Equal(package.Content, await aliased.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(package.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(10, ranged.Content.Headers.ContentLength);
        Assert.Equal(package.ContentLength, ranged.Content.Headers.ContentRange?.Length);
        Assert.Equal(package.Content[..10], await ranged.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, nuspec.StatusCode);
        Assert.Equal("text/xml", nuspec.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<id>Flat.Owner</id>", await nuspec.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, hash.StatusCode);
        Assert.Equal("text/plain", hash.Content.Headers.ContentType?.MediaType);
        Assert.Equal(package.PackageHash, await hash.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Extracted_flat_container_reads_are_read_your_writes_and_survive_restart()
    {
        using var directory = TemporaryDirectory.Create();
        var package = TestPackageBuilder.Create("Flat.Durable", "1.0.0").Build();

        await using (var first = await NuGetTestServerHost.StartAsync(directory.Path, PackageTransferLimits.Default))
        {
            using var upload = await first.HttpClient.PutAsync(
                "/package",
                new ByteArrayContent(package.Content));
            upload.EnsureSuccessStatusCode();

            using var immediateVersions = await first.HttpClient.GetAsync(
                "/flatcontainer/flat.durable/index.json");
            using var immediateContent = await first.HttpClient.GetAsync(
                "/flatcontainer/flat.durable/1.0.0/flat.durable.1.0.0.nupkg");

            Assert.Equal(HttpStatusCode.OK, immediateVersions.StatusCode);
            Assert.Equal(HttpStatusCode.OK, immediateContent.StatusCode);
            Assert.Equal(package.Content, await immediateContent.Content.ReadAsByteArrayAsync());

            using var unlist = await first.HttpClient.DeleteAsync("/package/Flat.Durable/1.0.0");
            Assert.Equal(HttpStatusCode.NoContent, unlist.StatusCode);
            using var unlistedContent = await first.HttpClient.GetAsync(
                "/flatcontainer/flat.durable/1.0.0/flat.durable.1.0.0.nupkg");
            Assert.Equal(HttpStatusCode.OK, unlistedContent.StatusCode);
        }

        await using var second = await NuGetTestServerHost.StartAsync(directory.Path, PackageTransferLimits.Default);
        using var restarted = await second.HttpClient.GetAsync(
            "/flatcontainer/flat.durable/1.0.0/flat.durable.1.0.0.nupkg");
        using var restartedHash = await second.HttpClient.GetAsync(
            "/flatcontainer/flat.durable/1.0.0/flat.durable.1.0.0.nupkg.sha512");

        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        Assert.Equal(package.Content, await restarted.Content.ReadAsByteArrayAsync());
        Assert.Equal(package.PackageHash, await restartedHash.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Real_nuget_clients_read_versions_content_and_nuspec_from_the_extension()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(
            TestPackageBuilder.Create("Flat.Client", "1.0.0")
                .WithFile("lib/net10.0/flat.txt", "content")
                .Build());

        var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>()
            ?? throw new InvalidOperationException("PackageBaseAddress was not discovered.");
        using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
        var versions = await resource.GetAllVersionsAsync(
            "flat.client",
            cache,
            NullLogger.Instance,
            CancellationToken.None);
        var dependencies = await resource.GetDependencyInfoAsync(
            "flat.client",
            NuGet.Versioning.NuGetVersion.Parse("1.0.0"),
            cache,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["1.0.0"], versions.Select(version => version.ToNormalizedString()));
        Assert.NotNull(dependencies);
        Assert.Equal("Flat.Client", dependencies.PackageIdentity.Id);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TemporaryDirectory Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.FunctionalTests",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
