using System.Net;
using System.Net.Http.Headers;
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
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "api/v1/models", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ProviderProbeResult(false, "LM Studio 返回 HTTP 401，需要 API Token。", Endpoint: endpoint, HttpStatus: 401, RequiresAuthentication: true);
            }

            return new ProviderProbeResult(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "LM Studio Server 已连接。" : $"LM Studio HTTP {(int)response.StatusCode}", Endpoint: endpoint, HttpStatus: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ProviderProbeResult(false, $"LM Studio 未连接: {exception.Message}", Endpoint: endpoint);
        }
    }

    public async Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        string[] routes = ["api/v1/models", "api/v0/models", "v1/models"];
        List<Exception> failures = [];
        for (int index = 0; index < routes.Length; index++)
        {
            try
            {
                using HttpResponseMessage response = await SendAsync(HttpMethod.Get, routes[index], cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("LM Studio API 返回 401，请配置 Token。");
                response.EnsureSuccessStatusCode();
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                usedFallback = index > 0;
                List<ModelProfile> models = index == 0 ? ParseNativeV1(document.RootElement) : ParseFallback(document.RootElement, routes[index]);
                if (models.Count > 0) return models;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
            {
                failures.Add(exception);
            }
        }

        throw new AggregateException("LM Studio models API 的 v1/v0/OpenAI fallback 均失败。", failures);
    }

    public async Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelProfile> models = await DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile? model = models.FirstOrDefault(item => item.Id == modelId);
        if (model is null) throw new InvalidOperationException("所选 LM Studio 模型已不存在，请刷新模型列表。");
        if (model.IsLoaded != true) throw new InvalidOperationException("所选模型未加载；首版不会自动加载模型。");
        if (model.ModelType is not null && !model.ModelType.Equals("llm", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"所选 loaded instance 类型为 {model.ModelType}，不是可生成文本的 LLM。");
        var tester = new ResponsesCompatibilityClient(httpClient, endpoint, tokenProvider);
        return await tester.TestAsync(ProviderKind.LmStudio, modelId, model.SupportsReasoning == true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, relative));
        string? token = tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    private static List<ModelProfile> ParseNativeV1(JsonElement root)
    {
        if (!root.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("/api/v1/models 缺少 models 数组。");
        }

        List<ModelProfile> result = [];
        foreach (JsonElement model in models.EnumerateArray())
        {
            string key = GetString(model, "key") ?? throw new InvalidDataException("LM Studio model 缺少 key。");
            string displayName = GetString(model, "display_name") ?? key;
            string? quantization = GetQuantization(model);
            string? parameters = GetString(model, "params_string");
            string? architecture = GetString(model, "architecture");
            string? modelType = GetString(model, "type");
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
                if (capabilities.TryGetProperty("reasoning", out JsonElement reasoningElement) && reasoningElement.ValueKind == JsonValueKind.Object)
                {
                    if (reasoningElement.TryGetProperty("allowed_options", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
                    {
                        reasoningOptions.AddRange(options.EnumerateArray().Select(item => item.GetString()).OfType<string>());
                        if (reasoningOptions.Count > 0)
                        {
                            reasoning = reasoningOptions.Any(option => !option.Equals("off", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
            }

            if (model.TryGetProperty("loaded_instances", out JsonElement instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0)
            {
                foreach (JsonElement instance in instances.EnumerateArray())
                {
                    string instanceId = GetString(instance, "id") ?? key;
                    int? context = instance.TryGetProperty("config", out JsonElement config) ? GetInt32(config, "context_length") : null;
                    result.Add(new ModelProfile(instanceId, displayName, ProviderKind.LmStudio, GetString(model, "description"), quantization, parameters, size, true, maximum, context, tools, reasoning, vision, reasoningOptions, "/api/v1/models", instanceId, Architecture: architecture, ModelType: modelType, SourceModelKey: key));
                }
            }
            else
            {
                result.Add(new ModelProfile(key, displayName, ProviderKind.LmStudio, GetString(model, "description"), quantization, parameters, size, false, maximum, null, tools, reasoning, vision, reasoningOptions, "/api/v1/models", Architecture: architecture, ModelType: modelType, SourceModelKey: key));
            }
        }

        return result;
    }

    private static List<ModelProfile> ParseFallback(JsonElement root, string source)
    {
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array) array = root;
        else if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array) array = data;
        else if (root.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array) array = models;
        else throw new InvalidDataException($"/{source} 未返回模型数组。");

        List<ModelProfile> result = [];
        foreach (JsonElement model in array.EnumerateArray())
        {
            string? id = GetString(model, "id") ?? GetString(model, "key") ?? GetString(model, "model");
            if (string.IsNullOrWhiteSpace(id)) continue;
            bool? loaded = GetBool(model, "loaded");
            result.Add(new ModelProfile(id, GetString(model, "display_name") ?? id, ProviderKind.LmStudio, IsLoaded: loaded, Source: "/" + source, ModelType: GetString(model, "type")));
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetInt32(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : null;
    private static long? GetInt64(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long number) ? number : null;
    private static bool? GetBool(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string? GetQuantization(JsonElement element)
    {
        if (!element.TryGetProperty("quantization", out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Object ? GetString(value, "name") : null;
    }

    private static Uri EnsureTrailingSlash(Uri value) => value.AbsoluteUri.EndsWith('/') ? value : new Uri(value.AbsoluteUri + "/");
}
