using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
    public async Task NativeApiCapturesSelectedVariantAndCompleteLoadConfiguration()
    {
        const string json = """
            {"models":[{"key":"qwen/qwen3.8-27b","selected_variant":"qwen/qwen3.8-27b@q8_0","variants":["qwen/qwen3.8-27b@q4_k_m","qwen/qwen3.8-27b@q8_0"],"type":"llm","format":"gguf","architecture":"qwen35","quantization":{"name":"Q8_0"},"max_context_length":262144,"loaded_instances":[{"id":"qwen/qwen3.8-27b","remaining_ttl_seconds":123,"config":{"context_length":32768,"eval_batch_size":4096,"physical_batch_size":512,"parallel":2,"flash_attention":true,"context_checkpoints":8,"reasoning_budget_message":"","speculative_draft_mtp":true,"speculative_draft_simple":false,"speculative_draft_model":"","speculative_draft_max_tokens":2,"speculative_draft_min_tokens":0,"speculative_draft_min_continue_probability":0.75,"offload_kv_cache_to_gpu":false}}]}]}
            """;
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(json)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        ModelProfile model = Assert.Single(await client.DiscoverNativeModelsAsync());

        Assert.Equal("qwen/qwen3.8-27b@q8_0", model.SelectedVariant);
        Assert.Equal(["qwen/qwen3.8-27b@q4_k_m", "qwen/qwen3.8-27b@q8_0"], model.AvailableVariants);
        Assert.Equal("gguf", model.Format);
        Assert.Equal(123, model.RemainingTtlSeconds);
        LmStudioLoadConfiguration config = Assert.IsType<LmStudioLoadConfiguration>(model.LoadedConfiguration);
        Assert.Equal(32_768, config.ContextLength);
        Assert.Equal(4_096, config.EvalBatchSize);
        Assert.Equal(512, config.PhysicalBatchSize);
        Assert.Equal(2, config.Parallel);
        Assert.True(config.FlashAttention);
        Assert.Equal(8, config.ContextCheckpoints);
        Assert.Equal(string.Empty, config.ReasoningBudgetMessage);
        Assert.True(config.SpeculativeDraftMtp);
        Assert.False(config.SpeculativeDraftSimple);
        Assert.Equal(string.Empty, config.SpeculativeDraftModel);
        Assert.Equal(2, config.SpeculativeDraftMaxTokens);
        Assert.Equal(0, config.SpeculativeDraftMinTokens);
        Assert.Equal(0.75, config.SpeculativeDraftMinContinueProbability);
        Assert.False(config.OffloadKvCacheToGpu);
    }

    [Fact]
    public async Task LoadSerializesFlatConfigAndPromptTemplateObjectAndUsesBearerToken()
    {
        string? requestJson = null;
        AuthenticationHeaderValue? authorization = null;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            authorization = request.Headers.Authorization;
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpHandler.Json("""
                {"instance_id":"qwen/qwen3.8-27b:2","status":"loaded","load_config":{"context_length":32768,"eval_batch_size":4096,"reasoning_budget_message":"","flash_attention":true}}
                """);
        }));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), () => "test-token", http);
        var config = new LmStudioLoadConfiguration(ContextLength: 32_768, EvalBatchSize: 4_096, FlashAttention: true, ReasoningBudgetMessage: string.Empty);

        LmStudioLoadResponse response = await client.LoadAsync(
            "qwen/qwen3.8-27b",
            config,
            new LmStudioPromptTemplateConfiguration("jinja", "{{ messages }}", []),
            120);

        Assert.Equal("qwen/qwen3.8-27b:2", response.InstanceId);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("test-token", authorization?.Parameter);
        using JsonDocument body = JsonDocument.Parse(requestJson!);
        JsonElement root = body.RootElement;
        Assert.Equal("qwen/qwen3.8-27b", root.GetProperty("model").GetString());
        Assert.Equal(32_768, root.GetProperty("context_length").GetInt32());
        Assert.Equal(4_096, root.GetProperty("eval_batch_size").GetInt32());
        Assert.True(root.GetProperty("flash_attention").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("reasoning_budget_message").GetString());
        Assert.Equal(120, root.GetProperty("ttl_seconds").GetInt32());
        Assert.True(root.GetProperty("echo_load_config").GetBoolean());
        Assert.False(root.TryGetProperty("config", out _));
        JsonElement prompt = root.GetProperty("prompt_template");
        Assert.Equal(JsonValueKind.Object, prompt.ValueKind);
        Assert.Equal("jinja", prompt.GetProperty("type").GetString());
        Assert.Equal("{{ messages }}", prompt.GetProperty("template").GetString());
        Assert.Equal(JsonValueKind.Array, prompt.GetProperty("stop_strings").ValueKind);
        Assert.False(root.TryGetProperty("physical_batch_size", out _));
    }

    [Fact]
    public async Task PromptTemplateSchemaProbeUsesTopLevelObjectAndRequiresModelNotFound()
    {
        string? requestJson = null;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpHandler.Json(
                """{"error":{"type":"model_not_found","code":"model_not_found","message":"not downloaded"}}""",
                HttpStatusCode.NotFound);
        }));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        await client.ProbePromptTemplateSchemaAsync();

        using JsonDocument body = JsonDocument.Parse(requestJson!);
        JsonElement root = body.RootElement;
        Assert.StartsWith("__cmm_schema_probe_", root.GetProperty("model").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("prompt_template").ValueKind);
        Assert.Equal("jinja", root.GetProperty("prompt_template").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("config", out _));
    }

    [Fact]
    public async Task StructuredApiErrorIsParsedWithoutPersistingTokenOrTemplate()
    {
        const string token = "secret-bearer-value";
        const string template = "patched-template-secret";
        string errorJson = JsonSerializer.Serialize(new
        {
            error = new
            {
                type = "model_not_found",
                code = "missing",
                param = "model",
                message = $"Bearer {token} rejected; template={template}",
            },
        });
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(errorJson, HttpStatusCode.NotFound)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), () => token, http);

        LmStudioApiException exception = await Assert.ThrowsAsync<LmStudioApiException>(() => client.LoadAsync(
            "qwen/missing",
            new LmStudioLoadConfiguration(ContextLength: 32_768),
            new LmStudioPromptTemplateConfiguration("jinja", template, [])));

        Assert.Equal(404, exception.Failure.HttpStatus);
        Assert.Equal("model_not_found", exception.Failure.ErrorType);
        Assert.Equal("missing", exception.Failure.ErrorCode);
        Assert.Equal("model", exception.Failure.Parameter);
        string serialized = JsonSerializer.Serialize(exception.Failure) + exception.Message;
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(template, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRejectsSuccessResponseWithoutLoadedStatus()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("""
            {"instance_id":"runtime-id","load_config":{"context_length":32768}}
            """)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.LoadAsync(
            "qwen/model",
            new LmStudioLoadConfiguration(ContextLength: 32_768)));

        Assert.Contains("<missing>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRejectsIdAliasAndRequiresAuthoritativeInstanceId()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json("""
            {"id":"guessed-id","status":"loaded","load_config":{"context_length":32768}}
            """)));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.LoadAsync(
            "qwen/model",
            new LmStudioLoadConfiguration(ContextLength: 32_768)));

        Assert.Contains("instance_id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnloadPostsOnlyExactInstanceId()
    {
        string? requestJson = null;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpHandler.Json("{}");
        }));
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);

        await client.UnloadAsync("qwen/qwen3.8-27b:2");

        using JsonDocument body = JsonDocument.Parse(requestJson!);
        JsonProperty property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("instance_id", property.Name);
        Assert.Equal("qwen/qwen3.8-27b:2", property.Value.GetString());
    }

    [Fact]
    public void HubVariantLocatorResolvesIndexedIdentifierUnderCustomDownloadsFolder()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "models");
        string gguf = Path.Combine(downloads, "lmstudio-community", "Qwen3.8-27B-GGUF", "Qwen3.8-27B-Q8_0.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(gguf)!);
        File.WriteAllText(gguf, "fixture");
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        const string variants = """
            [{"model":{"modelKey":"qwen/qwen3.8-27b","selectedVariant":"qwen/qwen3.8-27b@q8_0","architecture":"qwen35","quantization":{"name":"Q8_0"}},"variants":[{"modelKey":"qwen/qwen3.8-27b@q4_k_m","architecture":"qwen35","quantization":{"name":"Q4_K_M"},"indexedModelIdentifier":"qwen/qwen3.8-27b@repo/Q4.gguf"},{"modelKey":"qwen/qwen3.8-27b@q8_0","architecture":"qwen35","quantization":{"name":"Q8_0"},"path":"qwen/qwen3.8-27b","indexedModelIdentifier":"qwen/qwen3.8-27b@lmstudio-community/Qwen3.8-27B-GGUF/Qwen3.8-27B-Q8_0.gguf"}]}]
            """;
        var model = new ModelProfile(
            "qwen/qwen3.8-27b",
            "Qwen",
            ProviderKind.LmStudio,
            Quantization: "Q8_0",
            IsLoaded: true,
            Architecture: "qwen35",
            ModelType: "llm",
            SourceModelKey: "qwen/qwen3.8-27b",
            SelectedVariant: "qwen/qwen3.8-27b@q8_0");

        LmStudioModelFileResolution? resolution = LmStudioModelFileLocator.ResolveFromJson(model, variants, settings, temporary.Path);

        Assert.NotNull(resolution);
        Assert.Equal(Path.GetFullPath(gguf), resolution.FilePath);
        Assert.Equal("qwen/qwen3.8-27b@q8_0", resolution.SelectedVariant);
        Assert.Equal("Q8_0", resolution.Quantization);
    }

    [Fact]
    public void LocatorSupportsLegacyAbsoluteGgufPath()
    {
        using var temporary = new TemporaryDirectory();
        string gguf = Path.Combine(temporary.Path, "absolute-Q8_0.gguf");
        File.WriteAllText(gguf, "fixture");
        string variants = JsonSerializer.Serialize(new[]
        {
            new
            {
                modelKey = "qwen/root@q8_0",
                path = gguf,
                architecture = "qwen35",
                quantization = new { name = "Q8_0" },
            },
        });

        LmStudioModelFileResolution? resolution = LmStudioModelFileLocator.ResolveFromJson(
            CreateLocatorModel(),
            variants,
            null,
            temporary.Path);

        Assert.NotNull(resolution);
        Assert.Equal(Path.GetFullPath(gguf), resolution.FilePath);
    }

    [Fact]
    public void LocatorSupportsTraditionalRelativeModelsPath()
    {
        using var temporary = new TemporaryDirectory();
        string gguf = Path.Combine(temporary.Path, ".lmstudio", "models", "publisher", "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(gguf)!);
        File.WriteAllText(gguf, "fixture");
        const string variants = """
            [{"modelKey":"publisher/model","path":"publisher/model.gguf","architecture":"qwen35","quantization":{"name":"Q8_0"}}]
            """;
        ModelProfile model = CreateLocatorModel() with
        {
            SourceModelKey = "publisher/model",
            SelectedVariant = null,
        };

        LmStudioModelFileResolution? resolution = LmStudioModelFileLocator.ResolveFromJson(model, variants, null, temporary.Path);

        Assert.NotNull(resolution);
        Assert.Equal(Path.GetFullPath(gguf), resolution.FilePath);
    }

    [Fact]
    public void LocatorRejectsMultipleExactVariantCandidates()
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "one.gguf"), "one");
        File.WriteAllText(Path.Combine(downloads, "two.gguf"), "two");
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        const string variants = """
            [{"model":{"modelKey":"qwen/root"},"variants":[{"modelKey":"qwen/root@q8_0","path":"one.gguf","architecture":"qwen35","quantization":{"name":"Q8_0"}},{"modelKey":"qwen/root@q8_0","path":"two.gguf","architecture":"qwen35","quantization":{"name":"Q8_0"}}]}]
            """;

        LmStudioModelFileResolution? resolution = LmStudioModelFileLocator.ResolveFromJson(CreateLocatorModel(), variants, settings, temporary.Path);

        Assert.Null(resolution);
    }

    [Theory]
    [InlineData("Q4_K_M", "existing.gguf")]
    [InlineData("Q8_0", "missing.gguf")]
    public void LocatorRejectsQuantizationMismatchOrMissingFile(string quantization, string relativePath)
    {
        using var temporary = new TemporaryDirectory();
        string downloads = Path.Combine(temporary.Path, "downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "existing.gguf"), "fixture");
        string settings = JsonSerializer.Serialize(new { downloadsFolder = downloads });
        string variants = JsonSerializer.Serialize(new[]
        {
            new
            {
                modelKey = "qwen/root@q8_0",
                path = relativePath,
                architecture = "qwen35",
                quantization = new { name = quantization },
            },
        });

        LmStudioModelFileResolution? resolution = LmStudioModelFileLocator.ResolveFromJson(CreateLocatorModel(), variants, settings, temporary.Path);

        Assert.Null(resolution);
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
        Assert.Equal(4, responsesRequests);
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

    private static ModelProfile CreateLocatorModel() => new(
        "qwen/root",
        "Qwen",
        ProviderKind.LmStudio,
        Quantization: "Q8_0",
        IsLoaded: true,
        Architecture: "qwen35",
        ModelType: "llm",
        SourceModelKey: "qwen/root",
        SelectedVariant: "qwen/root@q8_0");
}
