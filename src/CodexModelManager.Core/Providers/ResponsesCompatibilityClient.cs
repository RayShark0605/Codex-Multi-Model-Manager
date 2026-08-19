using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public sealed class ResponsesCompatibilityClient
{
    private static readonly string[] RequiredValueProperty = ["value"];
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly Func<string?>? tokenProvider;
    private readonly string responsesPath;

    public ResponsesCompatibilityClient(HttpClient httpClient, Uri endpoint, Func<string?>? tokenProvider = null, string responsesPath = "v1/responses")
    {
        this.httpClient = httpClient;
        this.endpoint = endpoint;
        this.tokenProvider = tokenProvider;
        this.responsesPath = responsesPath;
    }

    public async Task<CompatibilityReport> TestAsync(
        ProviderKind provider,
        string model,
        bool testReasoning,
        CancellationToken cancellationToken = default)
    {
        List<CompatibilityResult> results = [];
        DateTimeOffset now = DateTimeOffset.Now;
        try
        {
            var hierarchyProbe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider, responsesPath);
            CodexInstructionHierarchyProbeResult hierarchy = await hierarchyProbe.ProbeAsync(model, cancellationToken).ConfigureAwait(false);
            results.Add(new CompatibilityResult(
                "Responses",
                hierarchy.ControlPassed ? CompatibilityStatus.Supported : CompatibilityStatus.Failed,
                hierarchy.ControlPassed
                    ? $"普通 Responses control 请求成功（HTTP {hierarchy.ControlHttpStatus}）。"
                    : hierarchy.Detail,
                hierarchy.CheckedAt,
                hierarchy.ControlPassed ? null : hierarchy.FailureCode));
            results.Add(new CompatibilityResult(
                "Codex Instruction Hierarchy",
                hierarchy.IsCompatible ? CompatibilityStatus.Supported : CompatibilityStatus.Failed,
                hierarchy.IsCompatible ? "instructions + developer + user 请求成功。" : hierarchy.Detail,
                hierarchy.CheckedAt,
                hierarchy.IsCompatible ? null : hierarchy.FailureCode));

            if (!hierarchy.IsCompatible)
            {
                bool templateFailure = hierarchy.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
                string blocked = templateFailure
                    ? "被 Codex 指令层级模板错误阻止，未继续发送重复请求。"
                    : "基础 Responses/Codex 指令层级预检失败，未继续发送依赖请求。";
                results.Add(new CompatibilityResult("Streaming", CompatibilityStatus.Untested, blocked, now, hierarchy.FailureCode));
                results.Add(new CompatibilityResult("Tool Calling", CompatibilityStatus.Untested, blocked, now, hierarchy.FailureCode));
                results.Add(new CompatibilityResult("Reasoning", CompatibilityStatus.Untested, blocked, now, hierarchy.FailureCode));
                return Complete(
                    provider,
                    model,
                    results,
                    now,
                    CompatibilityStatus.Failed,
                    "基础 Codex instructions/developer/user 请求失败，当前 loaded instance 无法启动可靠的 Codex Agent。",
                    hierarchy.FailureCode);
            }

            using HttpResponseMessage streaming = await SendAsync(new
            {
                model,
                instructions = "This is a harmless Codex streaming compatibility check.",
                input = CreateCodexShapedInput("Reply with exactly STREAM_OK."),
                max_output_tokens = 32,
                stream = true,
            }, cancellationToken).ConfigureAwait(false);
            string streamBody = await streaming.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool streamOk = streaming.IsSuccessStatusCode && streamBody.Split('\n').Any(line => line.StartsWith("data:", StringComparison.Ordinal));
            results.Add(new CompatibilityResult("Streaming", streamOk ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, streamOk ? "SSE data event 已返回。" : $"未检测到有效 SSE（HTTP {(int)streaming.StatusCode}）。", now));

            using HttpResponseMessage tools = await SendAsync(new
            {
                model,
                instructions = "Use the supplied harmless function when requested.",
                input = CreateCodexShapedInput("Call cmm_echo once with value CMM_TOOL_OK. Do not answer in plain text."),
                // Local reasoning models can consume the first 128 tokens before emitting
                // the required function_call. Keep enough budget to test the call itself.
                max_output_tokens = 512,
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        name = "cmm_echo",
                        description = "A harmless compatibility-test function.",
                        strict = true,
                        parameters = new
                        {
                            type = "object",
                            properties = new { value = new { type = "string" } },
                            required = RequiredValueProperty,
                            additionalProperties = false,
                        },
                    },
                },
                tool_choice = "required",
            }, cancellationToken).ConfigureAwait(false);
            string toolBody = await tools.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool validToolCall = tools.IsSuccessStatusCode && HasValidToolCall(toolBody);
            results.Add(new CompatibilityResult("Tool Calling", validToolCall ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, validToolCall ? "返回了合法 cmm_echo function_call 与参数。" : $"未返回合法 function_call（HTTP {(int)tools.StatusCode}）。", now));

            if (testReasoning)
            {
                // Do not map LM Studio's on/off load capability to a Codex effort.
                // Instead, send a normal Responses request and look for structured
                // reasoning evidence in the returned output.
                using HttpResponseMessage reasoning = await SendAsync(new
                {
                    model,
                    instructions = "This is a harmless Codex reasoning compatibility check.",
                    input = CreateCodexShapedInput("Think briefly, then answer exactly CMM_REASON_OK."),
                    max_output_tokens = 256,
                    stream = false,
                }, cancellationToken).ConfigureAwait(false);
                string reasoningBody = await reasoning.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                bool hasArtifact = reasoning.IsSuccessStatusCode && HasReasoningArtifact(reasoningBody);
                CompatibilityStatus reasoningStatus = hasArtifact
                    ? CompatibilityStatus.Supported
                    : reasoning.IsSuccessStatusCode ? CompatibilityStatus.LikelySupported : CompatibilityStatus.Failed;
                string reasoningDetail = hasArtifact
                    ? "响应包含结构化 reasoning artifact；未猜测 Codex effort 映射。"
                    : reasoning.IsSuccessStatusCode
                        ? "Reasoning 模型请求成功，但响应未暴露结构化 artifact；未猜测 effort 映射。"
                        : $"Reasoning 请求失败（HTTP {(int)reasoning.StatusCode}）。";
                results.Add(new CompatibilityResult("Reasoning", reasoningStatus, reasoningDetail, now));
            }
            else
            {
                results.Add(new CompatibilityResult("Reasoning", CompatibilityStatus.Untested, "服务未声明或未提供 reasoning 能力。", now));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            results.Add(new CompatibilityResult("Responses", CompatibilityStatus.Failed, "请求超时。", now));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            results.Add(new CompatibilityResult("Responses", CompatibilityStatus.Failed, $"{exception.GetType().Name}: {exception.Message}", now));
        }

        return Complete(provider, model, results, now);
    }

    private static CompatibilityReport Complete(
        ProviderKind provider,
        string model,
        List<CompatibilityResult> results,
        DateTimeOffset now,
        CompatibilityStatus agentStatus = CompatibilityStatus.Untested,
        string agentDetail = "需要主动运行 Level 3 临时目录测试。",
        string? agentFailureCode = null)
    {
        AddIfMissing(results, "Codex Agent", agentStatus, agentDetail, now, agentFailureCode);
        AddIfMissing(results, "Plan", CompatibilityStatus.Untested, "未通过真实 Codex agent 验证。", now);
        AddIfMissing(results, "Goal", CompatibilityStatus.Untested, "属于 App/模型工具协同能力，未自动宣称支持。", now);
        AddIfMissing(results, "MCP", CompatibilityStatus.KnownLimitation, "Custom/local Responses provider 的 namespace tool 兼容性需单独测试。", now);
        AddIfMissing(results, "Web Search", CompatibilityStatus.Untested, "Responses 连通不代表服务实现 Web Search。", now);
        AddIfMissing(results, "Image", CompatibilityStatus.Untested, "未发送图像输入。", now);
        AddIfMissing(results, "Computer Use", CompatibilityStatus.Untested, "未测试。", now);
        AddIfMissing(results, "Parallel Tools", CompatibilityStatus.Untested, "单工具测试不证明并行工具能力。", now);
        AddIfMissing(results, "Skills", CompatibilityStatus.Untested, "依赖 Codex 与模型 tool calling 的完整协作。", now);
        return new CompatibilityReport(provider, model, results);
    }

    private static void AddIfMissing(
        List<CompatibilityResult> results,
        string capability,
        CompatibilityStatus status,
        string detail,
        DateTimeOffset now,
        string? failureCode = null)
    {
        if (!results.Any(item => item.Capability == capability))
        {
            results.Add(new CompatibilityResult(capability, status, detail, now, failureCode));
        }
    }

    private static object[] CreateCodexShapedInput(string userText) =>
    [
        new { role = "developer", content = "Preserve the harmless CMM compatibility-test constraints." },
        new { role = "user", content = userText },
    ];

    private async Task<HttpResponseMessage> SendAsync<T>(T body, CancellationToken cancellationToken)
    {
        Uri uri = new(endpoint, responsesPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        string? token = tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasValidToolCall(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out JsonElement type) || type.GetString() != "function_call" ||
                    !item.TryGetProperty("name", out JsonElement name) || name.GetString() != "cmm_echo" ||
                    !item.TryGetProperty("arguments", out JsonElement arguments)) continue;
                string argumentJson = arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? string.Empty : arguments.GetRawText();
                using JsonDocument parsedArguments = JsonDocument.Parse(argumentJson);
                return parsedArguments.RootElement.TryGetProperty("value", out JsonElement value) && value.GetString() == "CMM_TOOL_OK";
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool HasReasoningArtifact(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (item.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() is string typeName && typeName.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) return true;
                if (item.TryGetProperty("reasoning_content", out JsonElement reasoningContent) &&
                    reasoningContent.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reasoningContent.GetString())) return true;
                if (item.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array &&
                    content.EnumerateArray().Any(part => part.TryGetProperty("type", out JsonElement partType) &&
                        partType.ValueKind == JsonValueKind.String && partType.GetString() is string partTypeName &&
                        partTypeName.Contains("reasoning", StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
