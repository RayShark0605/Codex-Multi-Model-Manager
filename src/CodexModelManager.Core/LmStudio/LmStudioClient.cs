using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Core.LmStudio;

public sealed class LmStudioClient : IModelProvider
{
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly Func<string?>? tokenProvider;
    private bool usedFallback;

    public LmStudioClient(Uri endpoint, Func<string?>? tokenProvider = null, HttpClient? httpClient = null)
    {
        LmStudioEndpointPolicy.Validate(endpoint);
        this.endpoint = EnsureTrailingSlash(endpoint);
        this.tokenProvider = tokenProvider;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public ProviderKind Kind => ProviderKind.LmStudio;

    public Uri Endpoint => endpoint;

    public bool UsedFallback => usedFallback;

    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "api/v1/models", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ProviderProbeResult(false, "LM Studio 返回 HTTP 401，需要 API Token。", Endpoint: endpoint, HttpStatus: 401, RequiresAuthentication: true);
            }

            return new ProviderProbeResult(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "LM Studio Server 已连接。" : $"LM Studio HTTP {(int)response.StatusCode}", Endpoint: endpoint, HttpStatus: (int)response.StatusCode);
        }
        catch (Exception exception) when (
            exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ProviderProbeResult(false, $"LM Studio 未连接: {exception.Message}", Endpoint: endpoint);
        }
    }

    public async Task<IReadOnlyList<ModelProfile>> DiscoverNativeModelsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "api/v1/models", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("LM Studio API 返回 401，请配置 Token。");
        }

        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        usedFallback = false;
        return ParseNativeV1(document.RootElement);
    }

    public async Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        string[] routes = ["api/v1/models", "api/v0/models", "v1/models"];
        List<Exception> failures = [];
        for (int index = 0; index < routes.Length; index++)
        {
            try
            {
                using HttpResponseMessage response = await SendAsync(HttpMethod.Get, routes[index], null, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new UnauthorizedAccessException("LM Studio API 返回 401，请配置 Token。");
                }

                response.EnsureSuccessStatusCode();
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                usedFallback = index > 0;
                List<ModelProfile> models = index == 0 ? ParseNativeV1(document.RootElement) : ParseFallback(document.RootElement, routes[index]);
                if (index == 0 || models.Count > 0)
                {
                    return models;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
            {
                failures.Add(exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
        }

        throw new AggregateException("LM Studio models API 的 v1/v0/OpenAI fallback 均失败。", failures);
    }

    public async Task UnloadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            "api/v1/models/unload",
            JsonContent.Create(new Dictionary<string, object?> { ["instance_id"] = instanceId }),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync("unload", response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ProbePromptTemplateSchemaAsync(CancellationToken cancellationToken = default)
    {
        string probeModel = "__cmm_schema_probe_" + Guid.NewGuid().ToString("N");
        Dictionary<string, object?> payload = CreateLoadPayload(
            probeModel,
            new LmStudioLoadConfiguration(),
            new LmStudioPromptTemplateConfiguration("jinja", "{{ messages }}", []),
            ttlSeconds: null);
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            "api/v1/models/load",
            JsonContent.Create(payload),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            LmStudioApiException exception = await CreateApiExceptionAsync("prompt_template schema probe", response, cancellationToken).ConfigureAwait(false);
            if (string.Equals(exception.Failure.ErrorType, "model_not_found", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw exception;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync("prompt_template schema probe", response, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidDataException("LM Studio Prompt Template schema 预检使用的随机不存在模型意外返回成功；为避免产生无法归因的实例，自动修复已阻断。");
    }

    public async Task<LmStudioLoadResponse> LoadAsync(
        string model,
        LmStudioLoadConfiguration configuration,
        LmStudioPromptTemplateConfiguration? promptTemplate = null,
        int? ttlSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(configuration);
        Dictionary<string, object?> payload = CreateLoadPayload(model, configuration, promptTemplate, ttlSeconds);
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            "api/v1/models/load",
            JsonContent.Create(payload),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync("load", response, cancellationToken, promptTemplate?.Template).ConfigureAwait(false);
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("LM Studio load 响应根节点不是对象。");
        }

        string? instanceId = GetString(root, "instance_id");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new InvalidDataException("LM Studio load 响应缺少 instance_id；拒绝猜测新实例 ID。");
        }

        string? status = GetString(root, "status");
        if (!string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"LM Studio load 响应状态不是 loaded: {status ?? "<missing>"}");
        }

        if (!root.TryGetProperty("load_config", out JsonElement loadConfig) || loadConfig.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("LM Studio load 响应未回显 load_config；无法验证实际加载参数。");
        }

        return new LmStudioLoadResponse(instanceId, status!, ParseLoadConfiguration(loadConfig));
    }

    public async Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelProfile> models = await DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile? model = models.FirstOrDefault(item => item.Id == modelId);
        if (model is null)
        {
            throw new InvalidOperationException("所选 LM Studio 模型已不存在，请刷新模型列表。");
        }

        if (model.IsLoaded != true)
        {
            throw new InvalidOperationException("所选模型未加载；兼容性测试不会隐式加载模型。");
        }

        if (model.ModelType is not null && !model.ModelType.Equals("llm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"所选 loaded instance 类型为 {model.ModelType}，不是可生成文本的 LLM。");
        }

        var tester = new ResponsesCompatibilityClient(httpClient, endpoint, tokenProvider);
        return await tester.TestAsync(ProviderKind.LmStudio, modelId, model.SupportsReasoning == true, cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<string, object?> CreateLoadPayload(
        string model,
        LmStudioLoadConfiguration configuration,
        LmStudioPromptTemplateConfiguration? promptTemplate,
        int? ttlSeconds)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["echo_load_config"] = true,
        };
        Add(payload, "context_length", configuration.ContextLength);
        Add(payload, "eval_batch_size", configuration.EvalBatchSize);
        Add(payload, "physical_batch_size", configuration.PhysicalBatchSize);
        Add(payload, "parallel", configuration.Parallel);
        Add(payload, "flash_attention", configuration.FlashAttention);
        Add(payload, "context_checkpoints", configuration.ContextCheckpoints);
        Add(payload, "reasoning_budget_message", configuration.ReasoningBudgetMessage);
        Add(payload, "speculative_draft_mtp", configuration.SpeculativeDraftMtp);
        Add(payload, "speculative_draft_simple", configuration.SpeculativeDraftSimple);
        Add(payload, "speculative_draft_model", configuration.SpeculativeDraftModel);
        Add(payload, "speculative_draft_max_tokens", configuration.SpeculativeDraftMaxTokens);
        Add(payload, "speculative_draft_min_tokens", configuration.SpeculativeDraftMinTokens);
        Add(payload, "speculative_draft_min_continue_probability", configuration.SpeculativeDraftMinContinueProbability);
        Add(payload, "offload_kv_cache_to_gpu", configuration.OffloadKvCacheToGpu);
        Add(payload, "num_experts", configuration.NumExperts);
        if (ttlSeconds is > 0)
        {
            payload["ttl_seconds"] = ttlSeconds.Value;
        }

        if (promptTemplate is not null)
        {
            if (!promptTemplate.Type.Equals("jinja", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(promptTemplate.Template))
            {
                throw new ArgumentException("LM Studio Prompt Template 必须是非空 jinja 对象。", nameof(promptTemplate));
            }

            payload["prompt_template"] = new Dictionary<string, object?>
            {
                ["type"] = "jinja",
                ["template"] = promptTemplate.Template,
                ["stop_strings"] = promptTemplate.StopStrings.ToArray(),
            };
        }

        return payload;
    }

    public static bool LoadConfigurationsEqual(LmStudioLoadConfiguration expected, LmStudioLoadConfiguration actual) =>
        expected.ContextLength == actual.ContextLength &&
        expected.EvalBatchSize == actual.EvalBatchSize &&
        expected.PhysicalBatchSize == actual.PhysicalBatchSize &&
        expected.Parallel == actual.Parallel &&
        expected.FlashAttention == actual.FlashAttention &&
        expected.ContextCheckpoints == actual.ContextCheckpoints &&
        string.Equals(expected.ReasoningBudgetMessage, actual.ReasoningBudgetMessage, StringComparison.Ordinal) &&
        expected.SpeculativeDraftMtp == actual.SpeculativeDraftMtp &&
        expected.SpeculativeDraftSimple == actual.SpeculativeDraftSimple &&
        string.Equals(expected.SpeculativeDraftModel, actual.SpeculativeDraftModel, StringComparison.Ordinal) &&
        expected.SpeculativeDraftMaxTokens == actual.SpeculativeDraftMaxTokens &&
        expected.SpeculativeDraftMinTokens == actual.SpeculativeDraftMinTokens &&
        NullableDoubleEquals(expected.SpeculativeDraftMinContinueProbability, actual.SpeculativeDraftMinContinueProbability) &&
        expected.OffloadKvCacheToGpu == actual.OffloadKvCacheToGpu &&
        expected.NumExperts == actual.NumExperts;

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relative,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, relative)) { Content = content };
        string? token = tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LmStudioApiException> CreateApiExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        params string?[] sensitiveValues)
    {
        string? errorType = null;
        string? errorCode = null;
        string? parameter = null;
        string? serverMessage = null;
        try
        {
            string body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.Object)
                    {
                        errorType = GetString(error, "type");
                        errorCode = GetString(error, "code");
                        parameter = GetString(error, "param");
                        serverMessage = GetString(error, "message");
                    }
                    else if (error.ValueKind == JsonValueKind.String)
                    {
                        serverMessage = error.GetString();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or OperationCanceledException)
        {
            // Never retain an unrecognized raw response body: future server
            // versions could echo request material such as a template.
        }

        List<string> secrets = sensitiveValues.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList();
        try
        {
            string? token = tokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(token))
            {
                secrets.Add(token);
            }
        }
        catch
        {
            // Failure formatting must not invoke credential-provider failures.
        }

        errorType = SanitizeField(errorType, 96, secrets, redactTemplateSyntax: false);
        errorCode = SanitizeField(errorCode, 96, secrets, redactTemplateSyntax: false);
        parameter = SanitizeField(parameter, 96, secrets, redactTemplateSyntax: false);
        string safeServerMessage = SanitizeField(serverMessage, 512, secrets, redactTemplateSyntax: true)
            ?? response.ReasonPhrase
            ?? "Unknown LM Studio error";
        string qualifier = !string.IsNullOrWhiteSpace(errorType)
            ? $" ({errorType}{(!string.IsNullOrWhiteSpace(errorCode) ? "/" + errorCode : string.Empty)})"
            : string.Empty;
        var failure = new LmStudioApiFailure(
            (int)response.StatusCode,
            errorType,
            errorCode,
            parameter,
            safeServerMessage);
        return new LmStudioApiException(
            failure,
            $"LM Studio {operation} 失败: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{qualifier}: {safeServerMessage}");
    }

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? SanitizeField(
        string? value,
        int maximumLength,
        IReadOnlyList<string> secrets,
        bool redactTemplateSyntax)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        foreach (string secret in secrets.OrderByDescending(secret => secret.Length))
        {
            safe = safe.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        if (redactTemplateSyntax &&
            (safe.Contains("{{", StringComparison.Ordinal) ||
             safe.Contains("{%", StringComparison.Ordinal) ||
             safe.Contains("prompt_template", StringComparison.OrdinalIgnoreCase) ||
             safe.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
             safe.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
             safe.Contains("api_key", StringComparison.OrdinalIgnoreCase)))
        {
            return "<redacted LM Studio error message>";
        }

        return Truncate(safe, maximumLength);
    }

    private static List<ModelProfile> ParseNativeV1(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("models", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("/api/v1/models 缺少 models 数组。");
        }

        List<ModelProfile> result = [];
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("LM Studio models 数组包含非对象条目。");
            }

            string key = GetString(model, "key") ?? throw new InvalidDataException("LM Studio model 缺少 key。");
            string displayName = GetString(model, "display_name") ?? key;
            string? quantization = GetQuantization(model);
            string? parameters = GetString(model, "params_string");
            string? architecture = GetString(model, "architecture");
            string? modelType = GetString(model, "type");
            string? selectedVariant = GetString(model, "selected_variant");
            IReadOnlyList<string> availableVariants = GetStringArray(model, "variants");
            string? format = GetString(model, "format");
            long? size = GetInt64(model, "size_bytes");
            int? maximum = GetInt32(model, "max_context_length");
            bool? tools = null;
            bool? vision = null;
            bool? reasoning = null;
            List<string> reasoningOptions = [];
            if (model.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Object)
            {
                tools = GetBool(capabilities, "trained_for_tool_use");
                vision = GetBool(capabilities, "vision");
                if (capabilities.TryGetProperty("reasoning", out JsonElement reasoningElement) && reasoningElement.ValueKind == JsonValueKind.Object &&
                    reasoningElement.TryGetProperty("allowed_options", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
                {
                    reasoningOptions.AddRange(options.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .OfType<string>());
                    if (reasoningOptions.Count > 0)
                    {
                        reasoning = reasoningOptions.Any(option => !option.Equals("off", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            if (model.TryGetProperty("loaded_instances", out JsonElement instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0)
            {
                foreach (JsonElement instance in instances.EnumerateArray())
                {
                    if (instance.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException("LM Studio loaded_instances 包含非对象条目。");
                    }

                    string instanceId = GetString(instance, "id") ?? key;
                    LmStudioLoadConfiguration configuration = instance.TryGetProperty("config", out JsonElement config) && config.ValueKind == JsonValueKind.Object
                        ? ParseLoadConfiguration(config)
                        : new LmStudioLoadConfiguration();
                    int? remainingTtl = GetInt32(instance, "remaining_ttl_seconds");
                    result.Add(new ModelProfile(
                        instanceId,
                        displayName,
                        ProviderKind.LmStudio,
                        GetString(model, "description"),
                        quantization,
                        parameters,
                        size,
                        true,
                        maximum,
                        configuration.ContextLength,
                        tools,
                        reasoning,
                        vision,
                        reasoningOptions,
                        "/api/v1/models",
                        instanceId,
                        Architecture: architecture,
                        ModelType: modelType,
                        SourceModelKey: key,
                        SelectedVariant: selectedVariant,
                        LoadedConfiguration: configuration,
                        RemainingTtlSeconds: remainingTtl,
                        AvailableVariants: availableVariants,
                        Format: format));
                }
            }
            else
            {
                result.Add(new ModelProfile(
                    key,
                    displayName,
                    ProviderKind.LmStudio,
                    GetString(model, "description"),
                    quantization,
                    parameters,
                    size,
                    false,
                    maximum,
                    null,
                    tools,
                    reasoning,
                    vision,
                    reasoningOptions,
                    "/api/v1/models",
                    Architecture: architecture,
                    ModelType: modelType,
                    SourceModelKey: key,
                    SelectedVariant: selectedVariant,
                    AvailableVariants: availableVariants,
                    Format: format));
            }
        }

        return result;
    }

    private static LmStudioLoadConfiguration ParseLoadConfiguration(JsonElement config) => new(
        GetInt32(config, "context_length"),
        GetInt32(config, "eval_batch_size"),
        GetInt32(config, "physical_batch_size"),
        GetInt32(config, "parallel"),
        GetBool(config, "flash_attention"),
        GetInt32(config, "context_checkpoints"),
        GetString(config, "reasoning_budget_message"),
        GetBool(config, "speculative_draft_mtp"),
        GetBool(config, "speculative_draft_simple"),
        GetString(config, "speculative_draft_model"),
        GetInt32(config, "speculative_draft_max_tokens"),
        GetInt32(config, "speculative_draft_min_tokens"),
        GetDouble(config, "speculative_draft_min_continue_probability"),
        GetBool(config, "offload_kv_cache_to_gpu"),
        GetInt32(config, "num_experts"));

    private static List<ModelProfile> ParseFallback(JsonElement root, string source)
    {
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            array = data;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
        {
            array = models;
        }
        else
        {
            throw new InvalidDataException($"/{source} 未返回模型数组。");
        }

        List<ModelProfile> result = [];
        foreach (JsonElement model in array.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"/{source} 模型数组包含非对象条目。");
            }

            string? id = GetString(model, "id") ?? GetString(model, "key") ?? GetString(model, "model");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            bool? loaded = GetBool(model, "loaded");
            result.Add(new ModelProfile(id, GetString(model, "display_name") ?? id, ProviderKind.LmStudio, IsLoaded: loaded, Source: "/" + source, ModelType: GetString(model, "type")));
        }

        return result;
    }

    private static void Add(Dictionary<string, object?> target, string name, object? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    private static bool NullableDoubleEquals(double? left, double? right) =>
        left is null ? right is null : right is not null && Math.Abs(left.Value - right.Value) <= 0.000000001d;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : null;

    private static long? GetInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number)
            ? number
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number)
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetQuantization(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("quantization", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object ? GetString(value, "name") : null;
    }

    private static Uri EnsureTrailingSlash(Uri value) => value.AbsoluteUri.EndsWith('/') ? value : new Uri(value.AbsoluteUri + "/");
}

public sealed record LmStudioLoadResponse(
    string InstanceId,
    string Status,
    LmStudioLoadConfiguration EchoedConfiguration);

public sealed class LmStudioApiException : InvalidOperationException
{
    public LmStudioApiException(LmStudioApiFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public LmStudioApiFailure Failure { get; }
}
