using System.Net;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LmStudioClientTests
{
    [Fact]
    public async Task NativeApiUsesLoadedInstanceContextNotMaximum()
    {
        const string json = """
            {"models":[{"key":"qwen/model@q6","display_name":"Qwen","type":"llm","architecture":"qwen35","quantization":{"name":"Q6_K","bits_per_weight":6},"params_string":"27B","size_bytes":17000000000,"max_context_length":262144,"capabilities":{"vision":false,"trained_for_tool_use":true,"reasoning":{"allowed_options":["off","on"],"default":"on"}},"loaded_instances":[{"id":"qwen/model@q6","config":{"context_length":65536}}]}]}
            """;
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        ModelProfile model = Assert.Single(await client.DiscoverModelsAsync());
        Assert.Equal(262_144, model.MaxContextLength);
        Assert.Equal(65_536, model.LoadedContextLength);
        Assert.True(model.IsLoaded);
        Assert.True(model.TrainedForToolUse);
        Assert.Equal("Q6_K", model.Quantization);
        Assert.Equal("qwen35", model.Architecture);
        Assert.Equal("llm", model.ModelType);
        Assert.Equal("qwen/model@q6", model.SourceModelKey);
        Assert.Contains("Context", model.SelectionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToOpenAiModelsAndLeavesCapabilitiesUnknown()
    {
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/v1/models", StringComparison.Ordinal) && !path.Contains("api/", StringComparison.Ordinal)
                ? StubHttpHandler.Json("{\"data\":[{\"id\":\"fallback-model\"}]}")
                : StubHttpHandler.Json("{}", HttpStatusCode.NotFound);
        }));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:7777"), null, http);
        ModelProfile model = Assert.Single(await client.DiscoverModelsAsync());
        Assert.True(client.UsedFallback);
        Assert.Null(model.MaxContextLength);
        Assert.Null(model.TrainedForToolUse);
        Assert.Null(model.SupportsReasoning);
    }

    [Fact]
    public async Task ProbeDetects401Authentication()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("{}", HttpStatusCode.Unauthorized)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        ProviderProbeResult probe = await client.ProbeAsync();
        Assert.True(probe.RequiresAuthentication);
        Assert.Equal(401, probe.HttpStatus);
    }

    [Fact]
    public async Task EmptyReasoningOptionsRemainUnknown()
    {
        const string json = """
            {"models":[{"key":"fixture","type":"llm","capabilities":{"reasoning":{"allowed_options":[]}},"loaded_instances":[]}]}
            """;
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        ModelProfile model = Assert.Single(await client.DiscoverModelsAsync());

        Assert.Null(model.SupportsReasoning);
        Assert.Empty(model.ReasoningOptions!);
    }

    [Fact]
    public void NonLoopbackHttpEndpointIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new LmStudioClient(new Uri("http://192.0.2.10:1234")));
    }

    [Theory]
    [InlineData("file:///C:/temp/lmstudio/")]
    [InlineData("ftp://localhost:1234/")]
    [InlineData("http://user:password@localhost:1234/")]
    [InlineData("http://localhost:1234/?token=secret")]
    [InlineData("http://localhost:1234/#fragment")]
    public void UnsafeOrCredentialBearingEndpointIsRejected(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => new LmStudioClient(new Uri(endpoint)));
    }

    [Fact]
    public async Task SwitchPreflightRevalidatesLoadedInstanceAndContextBeforeHierarchyRequests()
    {
        const string models = """
            {"models":[{"key":"fixture","type":"llm","loaded_instances":[{"id":"fixture@loaded","config":{"context_length":65536}}]}]}
            """;
        int modelsRequests = 0;
        int responsesRequests = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                modelsRequests++;
                return StubHttpHandler.Json(models);
            }

            responsesRequests++;
            return StubHttpHandler.Json("{\"output\":[]}");
        }));
        var preflight = new LmStudioSwitchPreflight(http);
        var request = new SwitchRequest(
            ProviderKind.LmStudio,
            "fixture@loaded",
            ContextWindow: 65_536,
            LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));

        CodexInstructionHierarchyProbeResult result = await preflight.ProbeAsync(request);

        Assert.True(result.IsCompatible);
        Assert.Equal(1, modelsRequests);
        Assert.Equal(2, responsesRequests);
    }

    [Fact]
    public async Task SwitchPreflightBlocksStaleLoadedContextWithoutSendingInference()
    {
        const string models = """
            {"models":[{"key":"fixture","type":"llm","loaded_instances":[{"id":"fixture@loaded","config":{"context_length":32768}}]}]}
            """;
        int responsesRequests = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(models);
            }

            responsesRequests++;
            return StubHttpHandler.Json("{\"output\":[]}");
        }));
        var preflight = new LmStudioSwitchPreflight(http);
        var request = new SwitchRequest(
            ProviderKind.LmStudio,
            "fixture@loaded",
            ContextWindow: 65_536,
            LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));

        CodexInstructionHierarchyProbeResult result = await preflight.ProbeAsync(request);

        Assert.False(result.IsCompatible);
        Assert.Equal(CompatibilityFailureCodes.LmStudioLoadedContextChanged, result.FailureCode);
        Assert.Equal(0, responsesRequests);
    }

    [Fact]
    public async Task SwitchPreflightBlocksMissingLoadedInstanceWithoutAutoloadRequest()
    {
        const string models = """
            {"models":[{"key":"fixture@loaded","type":"llm","loaded_instances":[]}]}
            """;
        int responsesRequests = 0;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(models);
            }

            responsesRequests++;
            return StubHttpHandler.Json("{\"output\":[]}");
        }));
        var preflight = new LmStudioSwitchPreflight(http);
        var request = new SwitchRequest(
            ProviderKind.LmStudio,
            "fixture@loaded",
            ContextWindow: 65_536,
            LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));

        CodexInstructionHierarchyProbeResult result = await preflight.ProbeAsync(request);

        Assert.False(result.IsCompatible);
        Assert.Equal(CompatibilityFailureCodes.LmStudioLoadedInstanceMissing, result.FailureCode);
        Assert.Equal(0, responsesRequests);
    }

    [Theory]
    [InlineData("The server is running on port 1234.", 1234)]
    [InlineData("Listening at http://localhost:5678", 5678)]
    [InlineData("not running", null)]
    public void LmsStatusPortParserIsNarrow(string output, int? expected)
    {
        Assert.Equal(expected, LmStudioEndpointDetector.ParsePort(output));
    }
}
