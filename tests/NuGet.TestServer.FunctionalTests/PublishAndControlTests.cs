using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.FunctionalTests;

public sealed class PublishAndControlTests
{
    [Fact]
    public async Task Request_body_limit_is_enforced_by_real_kestrel()
    {
        var limits = CreateLimits() with
        {
            MaxRequestBodyBytes = 512,
            MaxPackageBytes = 4096
        };
        await using var server = await NuGetTestServerHost.StartAsync(limits);
        var package = TestPackageBuilder.Create("Request.Too.Large", "1.0.0")
            .WithFile("large.bin", RandomNumberGenerator.GetBytes(2048))
            .Build();

        using var response = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(await server.Packages.FindAsync("Request.Too.Large", "1.0.0"));
    }

    [Fact]
    public async Task Package_limit_rejection_is_clear_and_does_not_activate_the_package()
    {
        var limits = CreateLimits() with { MaxPackageBytes = 512 };
        await using var server = await NuGetTestServerHost.StartAsync(limits);
        var package = TestPackageBuilder.Create("Package.Too.Large", "1.0.0")
            .WithFile("large.bin", RandomNumberGenerator.GetBytes(4096))
            .Build();

        using var response = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(
            "package size",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(await server.Packages.FindAsync("Package.Too.Large", "1.0.0"));
    }

    [Fact]
    public async Task Archive_limit_rejection_is_clear_and_does_not_activate_the_package()
    {
        var limits = CreateLimits() with { MaxArchiveEntryBytes = 128 };
        await using var server = await NuGetTestServerHost.StartAsync(limits);
        var package = TestPackageBuilder.Create("Entry.Too.Large", "1.0.0")
            .WithFile("large.bin", new byte[1024])
            .Build();

        using var response = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(
            "entry",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(await server.Packages.FindAsync("Entry.Too.Large", "1.0.0"));
    }

    [Fact]
    public async Task Malformed_package_rejection_is_clear()
    {
        await using var server = await NuGetTestServerHost.StartAsync(CreateLimits());

        using var response = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent("not a package"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "valid NuGet package",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Binary_control_upload_uses_the_same_streaming_limits()
    {
        await using var server = await NuGetTestServerHost.StartAsync(CreateLimits());
        var package = TestPackageBuilder.Create("Controlled.Stream", "1.0.0").Build();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__test/packages")
        {
            Content = new ByteArrayContent(package.Content)
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await server.HttpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(await server.Packages.FindAsync("Controlled.Stream", "1.0.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"content":null}""")]
    public async Task Malformed_control_json_returns_bad_request(string json)
    {
        await using var server = await NuGetTestServerHost.StartAsync(CreateLimits());
        using var content = new StringContent(
            json,
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await server.HttpClient.PostAsync("/__test/packages", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Canceled_upload_removes_its_partial_temporary_file()
    {
        using var temporary = TemporaryDirectory.Create();
        var limits = CreateLimits() with { TemporaryDirectory = temporary.Path };
        await using var server = await NuGetTestServerHost.StartAsync(limits);
        await using var source = new BlockingUploadStream();
        using var content = new StreamContent(source);
        content.Headers.ContentLength = 512 * 1024;
        using var cancellation = new CancellationTokenSource();

        var upload = server.HttpClient.PutAsync("/package", content, cancellation.Token);
        await WaitUntilAsync(
            () => Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(),
            TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        await WaitUntilAsync(
            () => !Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Uploaded_package_is_downloaded_as_a_stream()
    {
        await using var server = await NuGetTestServerHost.StartAsync(CreateLimits());
        var package = TestPackageBuilder.Create("Streamed.Package", "1.0.0")
            .WithFile("lib/net10.0/payload.bin", new byte[128 * 1024])
            .Build();
        using var upload = await server.HttpClient.PutAsync(
            "/package",
            new ByteArrayContent(package.Content));
        upload.EnsureSuccessStatusCode();

        using var download = await server.HttpClient.GetAsync(
            "/flatcontainer/streamed.package/1.0.0/streamed.package.1.0.0.nupkg",
            HttpCompletionOption.ResponseHeadersRead);
        await using var destination = new MemoryStream();
        await download.Content.CopyToAsync(destination);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(package.Content, destination.ToArray());
    }

    [Fact]
    public async Task Raw_nupkg_can_be_pushed_and_identical_duplicate_is_idempotent()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Pushed.Package", "1.0.0").Build();

        using var first = await server.HttpClient.PutAsync("/package", new ByteArrayContent(package.Content));
        using var duplicate = await server.HttpClient.PutAsync("/package", new ByteArrayContent(package.Content));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.NotNull(await server.Packages.FindAsync("pushed.package", "1.0.0"));
    }

    [Fact]
    public async Task Durable_kestrel_server_serves_a_package_after_restart()
    {
        using var directory = TemporaryDirectory.Create();
        var package = TestPackageBuilder.Create("Durable.Kestrel", "1.0.0")
            .WithPackageType("DotnetTool", "1.0.0")
            .Build();
        var symbols = TestPackageBuilder.Create("Durable.Kestrel", "1.0.0")
            .WithFile("lib/net10.0/Durable.Kestrel.pdb", [1, 2, 3, 4])
            .Build();
        await using (var first = await NuGetTestServerHost.StartAsync(
                         directory.Path,
                         CreateLimits()))
        {
            using var upload = await first.HttpClient.PutAsync(
                "/package",
                new ByteArrayContent(package.Content));
            upload.EnsureSuccessStatusCode();
            using var symbolUpload = await first.HttpClient.PutAsync(
                "/symbolpackage",
                new ByteArrayContent(symbols.Content));
            symbolUpload.EnsureSuccessStatusCode();
            using var metadata = await first.HttpClient.PutAsJsonAsync(
                "/__test/packages/Durable.Kestrel/1.0.0/metadata",
                new
                {
                    owners = new[] { "Alice" },
                    downloads = 42,
                    verified = true,
                    deprecation = new
                    {
                        reasons = new[] { "Legacy" },
                        message = "Use the replacement.",
                        alternatePackage = new
                        {
                            id = "Replacement.Package",
                            range = "[2.0.0,)"
                        }
                    }
                });
            metadata.EnsureSuccessStatusCode();
        }

        await using var second = await NuGetTestServerHost.StartAsync(
            directory.Path,
            CreateLimits());
        using var download = await second.HttpClient.GetAsync(
            "/flatcontainer/durable.kestrel/1.0.0/durable.kestrel.1.0.0.nupkg");
        using var search = await second.HttpClient.GetAsync("/query?packageType=dotnettool");
        using var registration = await second.HttpClient.GetAsync(
            "/registration/durable.kestrel/1.0.0.json");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(package.Content, await download.Content.ReadAsByteArrayAsync());
        search.EnsureSuccessStatusCode();
        registration.EnsureSuccessStatusCode();
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStreamAsync());
        using var registrationJson = JsonDocument.Parse(
            await registration.Content.ReadAsStreamAsync());
        Assert.Equal(
            "Durable.Kestrel",
            Assert.Single(searchJson.RootElement.GetProperty("data").EnumerateArray().ToArray())
                .GetProperty("id")
                .GetString());
        Assert.Equal(
            "Alice",
            Assert.Single(registrationJson.RootElement
                .GetProperty("catalogEntry")
                .GetProperty("owners")
                .EnumerateArray()
                .ToArray())
                .GetString());
        Assert.Equal(
            symbols.Content,
            await second.Packages.FindSymbolAsync("Durable.Kestrel", "1.0.0"));
    }

    [Fact]
    public async Task Control_api_can_add_list_and_reset_packages()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        var package = TestPackageBuilder.Create("Controlled.Package", "1.0.0").Build();

        using var add = await server.HttpClient.PostAsJsonAsync("/__test/packages", new
        {
            content = Convert.ToBase64String(package.Content)
        });
        add.EnsureSuccessStatusCode();

        var packages = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/packages");
        Assert.Single(packages!);

        using var reset = await server.HttpClient.PostAsync("/__test/reset", null);
        reset.EnsureSuccessStatusCode();
        packages = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/packages");
        Assert.Empty(packages!);
    }

    [Fact]
    public async Task Faults_are_deterministic_and_requests_are_recorded()
    {
        await using var server = await NuGetTestServerHost.StartAsync();
        await server.Packages.AddAsync(TestPackageBuilder.Create("Example", "1.0.0").Build());
        await server.Faults.AddAsync(new FaultRule(
            Id: "fail-twice",
            Method: "GET",
            PathContains: "/flatcontainer/example/1.0.0/",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 2,
            Delay: TimeSpan.Zero));

        const string path = "/flatcontainer/example/1.0.0/example.1.0.0.nupkg";
        using var first = await server.HttpClient.GetAsync(path);
        using var second = await server.HttpClient.GetAsync(path);
        using var third = await server.HttpClient.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var requests = await server.Requests.GetAsync();
        var attempts = requests.Where(r => r.Path == path).ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.Equal(["fail-twice", "fail-twice", null], attempts.Select(r => r.FaultRuleId));
        Assert.Equal([503, 503, 200], attempts.Select(r => r.StatusCode));
    }

    [Fact]
    public async Task Control_api_reports_bounded_runtime_state_and_reset_clears_it()
    {
        await using var server = await NuGetTestServerHost.StartAsync(
            new RuntimeStateConfiguration(requestHistoryCapacity: 3, faultRuleCapacity: 2));

        for (var index = 0; index < 5; index++)
        {
            using var health = await server.HttpClient.GetAsync("/__test/health");
            health.EnsureSuccessStatusCode();
        }

        var requests = await server.HttpClient.GetFromJsonAsync<JsonElement[]>("/__test/requests");
        Assert.Equal(3, requests!.Length);

        using var firstFault = await AddFaultAsync(server, "first");
        using var secondFault = await AddFaultAsync(server, "second");
        using var rejectedFault = await AddFaultAsync(server, "third");
        Assert.Equal(HttpStatusCode.Created, firstFault.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondFault.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejectedFault.StatusCode);

        var state = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/state");
        Assert.Equal(3, state.GetProperty("requestCount").GetInt32());
        Assert.True(state.GetProperty("evictedRequestCount").GetInt64() >= 3);
        Assert.Equal(3, state.GetProperty("requestCapacity").GetInt32());
        Assert.Equal(2, state.GetProperty("faultCount").GetInt32());
        Assert.Equal(2, state.GetProperty("faultCapacity").GetInt32());

        using var reset = await server.HttpClient.PostAsync("/__test/reset", null);
        reset.EnsureSuccessStatusCode();

        state = await server.HttpClient.GetFromJsonAsync<JsonElement>("/__test/state");
        Assert.Equal(0, state.GetProperty("requestCount").GetInt32());
        Assert.Equal(0, state.GetProperty("evictedRequestCount").GetInt64());
        Assert.Equal(0, state.GetProperty("faultCount").GetInt32());
    }

    private static Task<HttpResponseMessage> AddFaultAsync(
        NuGetTestServerHost server,
        string id) =>
        server.HttpClient.PostAsJsonAsync("/__test/faults", new FaultRule(
            Id: id,
            Method: "GET",
            PathContains: "/flatcontainer/",
            StatusCode: HttpStatusCode.ServiceUnavailable,
            RemainingMatches: 1,
            Delay: TimeSpan.Zero));

    private static PackageTransferLimits CreateLimits() => new()
    {
        MaxRequestBodyBytes = 1024 * 1024,
        MaxPackageBytes = 512 * 1024,
        MaxArchiveEntries = 100,
        MaxArchiveEntryBytes = 256 * 1024,
        MaxExpandedArchiveBytes = 512 * 1024
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(25, cancellation.Token);
        }
    }

    private sealed class BlockingUploadStream : Stream
    {
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sentPrefix)
            {
                _sentPrefix = true;
                buffer.Span.Fill(42);
                return buffer.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
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
