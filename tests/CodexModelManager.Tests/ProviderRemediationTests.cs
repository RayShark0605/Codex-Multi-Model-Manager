using System.Diagnostics;
using System.Net;
using System.Text;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Tests;

public sealed class ProviderRemediationTests
{
    [Fact]
    public async Task HierarchyTimeoutCoversAStalledResponseBody()
    {
        using var http = new HttpClient(new AsyncStubHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverEndingStream()),
            })));
        var probe = new CodexInstructionHierarchyProbe(
            http,
            new Uri("http://127.0.0.1:1234/"),
            requestTimeout: TimeSpan.FromMilliseconds(120));
        var stopwatch = Stopwatch.StartNew();

        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync("fixture");

        Assert.Equal(CompatibilityFailureCodes.Timeout, result.FailureCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task StreamingReturnsAfterFirstDataEventWithoutWaitingForConnectionClose()
    {
        int requestCount = 0;
        using var http = new HttpClient(new AsyncStubHttpHandler((_, _) => Task.FromResult(++requestCount switch
        {
            <= 4 => StubHttpHandler.Json("{\"output\":[]}"),
            5 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FirstChunkThenBlockStream("event: response.output_text.delta\ndata: {\"delta\":\"ok\"}\n")),
            },
            6 => StubHttpHandler.Json("\"not-an-object\""),
            _ => throw new InvalidOperationException("Unexpected request."),
        })));
        var client = new ResponsesCompatibilityClient(
            http,
            new Uri("http://127.0.0.1:1234"),
            stageTimeout: TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        CompatibilityReport report = await client.TestAsync(ProviderKind.LmStudio, "fixture", true);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
        Assert.Equal(6, requestCount);
        Assert.Equal(CompatibilityStatus.Supported, Single(report, "Streaming").Status);
        Assert.Equal(CompatibilityStatus.Failed, Single(report, "Tool Calling").Status);
        Assert.Equal(CompatibilityStatus.Untested, Single(report, "Reasoning").Status);
        Assert.Single(report.Results, item => item.Capability == "Responses");
    }

    [Fact]
    public async Task ToolBodyTimeoutIsAttributedOnlyToToolStage()
    {
        int requestCount = 0;
        using var http = new HttpClient(new AsyncStubHttpHandler((_, _) => Task.FromResult(++requestCount switch
        {
            <= 4 => StubHttpHandler.Json("{\"output\":[]}"),
            5 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {}\n") },
            6 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) },
            _ => throw new InvalidOperationException("Unexpected request."),
        })));
        var client = new ResponsesCompatibilityClient(
            http,
            new Uri("http://127.0.0.1:1234/"),
            stageTimeout: TimeSpan.FromMilliseconds(120));

        CompatibilityReport report = await client.TestAsync(ProviderKind.LmStudio, "fixture", true);

        Assert.Equal(CompatibilityStatus.Supported, Single(report, "Responses").Status);
        Assert.Equal(CompatibilityStatus.Supported, Single(report, "Streaming").Status);
        Assert.Equal(CompatibilityStatus.Failed, Single(report, "Tool Calling").Status);
        Assert.Contains("超时", Single(report, "Tool Calling").Detail, StringComparison.Ordinal);
        Assert.Equal(CompatibilityStatus.Untested, Single(report, "Reasoning").Status);
        Assert.Single(report.Results, item => item.Capability == "Responses");
    }

    [Fact]
    public async Task NonObjectReasoningJsonIsAStableStageFailure()
    {
        int requestCount = 0;
        using var http = new HttpClient(new StubHttpHandler(_ => ++requestCount switch
        {
            <= 4 => StubHttpHandler.Json("{\"output\":[]}"),
            5 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {}\n") },
            6 => StubHttpHandler.Json("{\"output\":[{\"type\":\"function_call\",\"name\":\"cmm_echo\",\"arguments\":{\"value\":\"CMM_TOOL_OK\"}}]}"),
            7 => StubHttpHandler.Json("[1,2,3]"),
            _ => throw new InvalidOperationException("Unexpected request."),
        }));
        var client = new ResponsesCompatibilityClient(http, new Uri("http://127.0.0.1:1234/"));

        CompatibilityReport report = await client.TestAsync(ProviderKind.LmStudio, "fixture", true);

        Assert.Equal(CompatibilityStatus.Failed, Single(report, "Reasoning").Status);
        Assert.Contains("JSON 结构", Single(report, "Reasoning").Detail, StringComparison.Ordinal);
        Assert.Equal(7, requestCount);
    }

    [Theory]
    [InlineData("http://example.test:1234/")]
    [InlineData("http://user:pass@localhost:1234/")]
    [InlineData("http://localhost:1234/?key=value")]
    [InlineData("file:///C:/temp/")]
    public void ResponsesClientRejectsUnsafeEndpoint(string endpoint)
    {
        using var http = new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException()));
        Assert.Throws<InvalidOperationException>(() => new ResponsesCompatibilityClient(http, new Uri(endpoint)));
    }

    [Theory]
    [InlineData("https://other.test/v1/responses")]
    [InlineData("//other.test/v1/responses")]
    [InlineData("\\\\other.test\\v1\\responses")]
    public void ResponsesClientRejectsPathThatCanOverrideAuthority(string responsesPath)
    {
        using var http = new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException()));
        Assert.Throws<ArgumentException>(() => new ResponsesCompatibilityClient(
            http,
            new Uri("http://127.0.0.1:1234/base"),
            responsesPath: responsesPath));
    }

    [Fact]
    public async Task ResponsesClientNormalizesBaseUriTrailingSlash()
    {
        Uri? requested = null;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requested = request.RequestUri;
            return StubHttpHandler.Json("{\"output\":null}");
        }));
        var client = new ResponsesCompatibilityClient(
            http,
            new Uri("http://127.0.0.1:1234/base"),
            responsesPath: "v1/responses");

        await client.TestAsync(ProviderKind.LmStudio, "fixture", false);

        Assert.Equal("http://127.0.0.1:1234/base/v1/responses", requested?.AbsoluteUri);
    }

    [Fact]
    public async Task NativeModelsSuccessWithEmptyListDoesNotProbeFallbackRoutes()
    {
        int requestCount = 0;
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            requestCount++;
            return StubHttpHandler.Json("{\"models\":[]}");
        }));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        IReadOnlyList<ModelProfile> models = await client.DiscoverModelsAsync();

        Assert.Empty(models);
        Assert.False(client.UsedFallback);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task LmStudioProbePropagatesCallerCancellation()
    {
        using var http = new HttpClient(new AsyncStubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ProbeAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"ok\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task NativeModelsRejectNonObjectRootWithoutInvalidOperationEscape(string json)
    {
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DiscoverNativeModelsAsync());
    }

    [Fact]
    public async Task NativeModelsNumericFieldsRejectWrongKindsWithoutInvalidOperationEscape()
    {
        const string json = """
            {
              "models": [
                {
                  "key": "fixture",
                  "max_context_length": "not-a-number",
                  "size_bytes": {},
                  "loaded_instances": [
                    {
                      "id": "fixture@loaded",
                      "remaining_ttl_seconds": [],
                      "config": {
                        "context_length": "not-a-number",
                        "speculative_draft_min_continue_probability": "not-a-number"
                      }
                    }
                  ]
                }
              ]
            }
            """;
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync();

        ModelProfile model = Assert.Single(models);
        Assert.Null(model.MaxContextLength);
        Assert.Null(model.SizeBytes);
        Assert.Null(model.RemainingTtlSeconds);
        Assert.Null(model.LoadedContextLength);
        Assert.Null(model.LoadedConfiguration?.SpeculativeDraftMinContinueProbability);
    }

    [Fact]
    public async Task SwitchPreflightDoesNotMaskNativeFailureWithFallbackData()
    {
        int requestCount = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requestCount++;
            return request.RequestUri!.AbsolutePath.Contains("api/v1/models", StringComparison.Ordinal)
                ? StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
                : StubHttpHandler.Json("{\"data\":[{\"id\":\"fixture@loaded\"}]}");
        }));
        var preflight = new LmStudioSwitchPreflight(http);
        var request = new SwitchRequest(
            ProviderKind.LmStudio,
            "fixture@loaded",
            ContextWindow: 65_536,
            LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));

        CodexInstructionHierarchyProbeResult result = await preflight.ProbeAsync(request);

        Assert.Equal(CompatibilityFailureCodes.OtherProviderError, result.FailureCode);
        Assert.NotEqual(CompatibilityFailureCodes.LmStudioLoadedInstanceMissing, result.FailureCode);
        Assert.Equal(1, requestCount);
    }

    [Theory]
    [InlineData("The server is running on port 1234.", 1234)]
    [InlineData("port: 2345", 2345)]
    [InlineData("port=3456", 3456)]
    [InlineData("Listening at http://127.0.0.1:4567/v1", 4567)]
    [InlineData("{\"port\":5678}", 5678)]
    [InlineData("port: 0", null)]
    [InlineData("port: 65536", null)]
    [InlineData("port: 1234 then localhost:5678", null)]
    public void LmsPortParserSupportsKnownFormatsAndRejectsConflicts(string output, int? expected)
    {
        Assert.Equal(expected, LmStudioEndpointDetector.ParsePort(output));
    }

    [Fact]
    public void LmsProcessStartInformationUsesUtf8ForEveryRedirectedStream()
    {
        ProcessStartInfo locator = LmStudioModelFileLocator.CreateLmsProcessStartInfo(
            "lms.exe",
            ["ps", "--json"]);
        ProcessStartInfo endpoint = LmStudioEndpointDetector.CreateLmsStatusStartInfo("lms.exe");

        foreach (ProcessStartInfo start in new[] { locator, endpoint })
        {
            Assert.True(start.RedirectStandardInput);
            Assert.True(start.RedirectStandardOutput);
            Assert.True(start.RedirectStandardError);
            Assert.Equal(Encoding.UTF8.WebName, start.StandardInputEncoding?.WebName);
            Assert.Equal(Encoding.UTF8.WebName, start.StandardOutputEncoding?.WebName);
            Assert.Equal(Encoding.UTF8.WebName, start.StandardErrorEncoding?.WebName);
        }

        Assert.Equal(["ps", "--json"], locator.ArgumentList.Cast<string>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"models\":{}}")]
    [InlineData("{\"models\":[{\"slug\":\"same\",\"context_window\":1,\"minimal_client_version\":\"1.0\"},{\"slug\":\"same\",\"context_window\":1,\"minimal_client_version\":\"1.0\"}]}")]
    [InlineData("{\"models\":[{\"slug\":\"one\",\"context_window\":1,\"minimal_client_version\":\"1.0\",\"input_modalities\":{}}]}")]
    public void DeepSeekCatalogRejectsInvalidShapesAndDuplicateSlugs(string json)
    {
        Assert.Throws<InvalidDataException>(() => DeepSeekCatalogService.ValidateCatalog(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task DeepSeekCatalogPropagatesCallerCancellationInsteadOfUsingSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(temporary.Path, "home"));
        using var http = new HttpClient(new AsyncStubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }));
        var service = new DeepSeekCatalogService(home, new CodexModelManager.Core.Infrastructure.AppPaths(
            Path.Combine(temporary.Path, "local")), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureDeepSeekCatalogAsync(cancellation.Token));
    }

    private static CompatibilityResult Single(CompatibilityReport report, string capability) =>
        Assert.Single(report.Results, item => item.Capability == capability);

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FirstChunkThenBlockStream(string firstChunk) : Stream
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(firstChunk);
        private bool returnedFirstChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (!returnedFirstChunk)
            {
                returnedFirstChunk = true;
                bytes.AsSpan().CopyTo(buffer.AsSpan(offset, count));
                return bytes.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!returnedFirstChunk)
            {
                returnedFirstChunk = true;
                bytes.AsSpan().CopyTo(buffer.Span);
                return bytes.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
