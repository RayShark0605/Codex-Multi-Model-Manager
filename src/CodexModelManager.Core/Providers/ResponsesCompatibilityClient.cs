using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public sealed class ResponsesCompatibilityClient
{
    private const int MaximumStreamingBodyBytes = 64 * 1024;
    private const int MaximumJsonBodyBytes = 1024 * 1024;
    private static readonly string[] RequiredValueProperty = ["value"];
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly Func<string?>? tokenProvider;
    private readonly string responsesPath;
    private readonly TimeSpan stageTimeout;

    public ResponsesCompatibilityClient(
        HttpClient httpClient,
        Uri endpoint,
        Func<string?>? tokenProvider = null,
        string responsesPath = "v1/responses",
        TimeSpan? stageTimeout = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.endpoint = NormalizeEndpoint(endpoint);
        this.tokenProvider = tokenProvider;
        this.responsesPath = NormalizeResponsesPath(this.endpoint, responsesPath);
        this.stageTimeout = stageTimeout ?? TimeSpan.FromSeconds(45);
        if (this.stageTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stageTimeout), "单阶段超时必须为正数。");
        }
    }

    public async Task<CompatibilityReport> TestAsync(
        ProviderKind provider,
        string model,
        bool testReasoning,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        List<CompatibilityResult> results = [];
        DateTimeOffset now = DateTimeOffset.Now;
        var hierarchyProbe = new CodexInstructionHierarchyProbe(
            httpClient,
            endpoint,
            tokenProvider,
            responsesPath,
            stageTimeout);
        CodexInstructionHierarchyProbeResult hierarchy = await hierarchyProbe.ProbeAsync(model, cancellationToken).ConfigureAwait(false);
        Upsert(results, new CompatibilityResult(
            "Responses",
            hierarchy.ControlPassed ? CompatibilityStatus.Supported : CompatibilityStatus.Failed,
            hierarchy.ControlPassed
                ? $"普通 Responses control 请求成功（HTTP {hierarchy.ControlHttpStatus}）。"
                : hierarchy.Detail,
            hierarchy.CheckedAt,
            hierarchy.ControlPassed ? null : hierarchy.FailureCode));
        Upsert(results, new CompatibilityResult(
            "Codex Instruction Hierarchy",
            hierarchy.IsCompatible ? CompatibilityStatus.Supported : CompatibilityStatus.Failed,
            hierarchy.IsCompatible ? "Basic、前导 developer、多轮 control 与后置 developer 四阶段请求成功。" : hierarchy.Detail,
            hierarchy.CheckedAt,
            hierarchy.IsCompatible ? null : hierarchy.FailureCode));
        AddProbeStep(results, "Basic Control", hierarchy.Control, hierarchy, hierarchy.CheckedAt);
        AddProbeStep(results, "Leading Developer", hierarchy.LeadingDeveloper, hierarchy, hierarchy.CheckedAt);
        AddProbeStep(results, "Conversation Control", hierarchy.ConversationControl, hierarchy, hierarchy.CheckedAt);
        AddProbeStep(results, "Continuation Developer", hierarchy.ContinuationDeveloper, hierarchy, hierarchy.CheckedAt);

        if (!hierarchy.IsCompatible)
        {
            bool templateFailure = hierarchy.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or
                CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or
                CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder;
            string blocked = templateFailure
                ? "被 Codex 指令层级模板错误阻止，未继续发送重复请求。"
                : "基础 Responses/Codex 指令层级预检失败，未继续发送依赖请求。";
            MarkUntested(results, ["Streaming", "Tool Calling", "Reasoning"], blocked, hierarchy.FailureCode, now);
            return Complete(
                provider,
                model,
                results,
                now,
                CompatibilityStatus.Failed,
                "四阶段 Codex 指令层级请求未全部通过，当前 loaded instance 无法启动可靠的 Codex Agent。",
                hierarchy.FailureCode);
        }

        CompatibilityResult streaming = await RunStreamingStageAsync(model, cancellationToken).ConfigureAwait(false);
        Upsert(results, streaming);
        if (streaming.Status != CompatibilityStatus.Supported)
        {
            MarkUntested(results, ["Tool Calling", "Reasoning"], "Streaming 阶段失败，后续依赖阶段未运行。", null, now);
            return Complete(provider, model, results, now);
        }

        CompatibilityResult tools = await RunToolStageAsync(model, cancellationToken).ConfigureAwait(false);
        Upsert(results, tools);
        if (tools.Status != CompatibilityStatus.Supported)
        {
            MarkUntested(results, ["Reasoning"], "Tool Calling 阶段失败，后续 Reasoning 阶段未运行。", null, now);
            return Complete(provider, model, results, now);
        }

        if (testReasoning)
        {
            Upsert(results, await RunReasoningStageAsync(model, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            Upsert(results, new CompatibilityResult("Reasoning", CompatibilityStatus.Untested, "服务未声明或未提供 reasoning 能力。", now));
        }

        return Complete(provider, model, results, now);
    }

    private async Task<CompatibilityResult> RunStreamingStageAsync(string model, CancellationToken cancellationToken)
    {
        DateTimeOffset checkedAt = DateTimeOffset.Now;
        try
        {
            using var timeout = CreateStageTimeout(cancellationToken);
            using HttpRequestMessage request = CreateRequest(new
            {
                model,
                instructions = "This is a harmless Codex streaming compatibility check.",
                input = CreateCodexShapedInput("Reply with exactly STREAM_OK."),
                max_output_tokens = 32,
                stream = true,
            });
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            bool hasData = response.IsSuccessStatusCode &&
                await HasSseDataEventAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return new CompatibilityResult(
                "Streaming",
                hasData ? CompatibilityStatus.Supported : CompatibilityStatus.Failed,
                hasData ? "SSE data event 已返回。" : $"未检测到有效 SSE（HTTP {(int)response.StatusCode}）。",
                checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CompatibilityResult("Streaming", CompatibilityStatus.Failed, "Streaming 阶段在完整响应读取前超时。", checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return new CompatibilityResult("Streaming", CompatibilityStatus.Failed, $"Streaming 阶段协议失败（{exception.GetType().Name}）。", checkedAt);
        }
    }

    private async Task<CompatibilityResult> RunToolStageAsync(string model, CancellationToken cancellationToken)
    {
        DateTimeOffset checkedAt = DateTimeOffset.Now;
        try
        {
            using var timeout = CreateStageTimeout(cancellationToken);
            using HttpRequestMessage request = CreateRequest(new
            {
                model,
                instructions = "Use the supplied harmless function when requested.",
                input = CreateCodexShapedInput("Call cmm_echo once with value CMM_TOOL_OK. Do not answer in plain text."),
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
            });
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            string body = await ReadLimitedUtf8BodyAsync(response.Content, MaximumJsonBodyBytes, timeout.Token).ConfigureAwait(false);
            bool valid = response.IsSuccessStatusCode && HasValidToolCall(body);
            return new CompatibilityResult(
                "Tool Calling",
                valid ? CompatibilityStatus.Supported : CompatibilityStatus.Failed,
                valid ? "返回了合法 cmm_echo function_call 与参数。" : $"未返回合法 function_call（HTTP {(int)response.StatusCode}）。",
                checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CompatibilityResult("Tool Calling", CompatibilityStatus.Failed, "Tool Calling 阶段在完整响应读取前超时。", checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            return new CompatibilityResult("Tool Calling", CompatibilityStatus.Failed, $"Tool Calling 阶段协议失败（{exception.GetType().Name}）。", checkedAt);
        }
    }

    private async Task<CompatibilityResult> RunReasoningStageAsync(string model, CancellationToken cancellationToken)
    {
        DateTimeOffset checkedAt = DateTimeOffset.Now;
        try
        {
            using var timeout = CreateStageTimeout(cancellationToken);
            using HttpRequestMessage request = CreateRequest(new
            {
                model,
                instructions = "This is a harmless Codex reasoning compatibility check.",
                input = CreateCodexShapedInput("Think briefly, then answer exactly CMM_REASON_OK."),
                max_output_tokens = 256,
                stream = false,
            });
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            string body = await ReadLimitedUtf8BodyAsync(response.Content, MaximumJsonBodyBytes, timeout.Token).ConfigureAwait(false);
            bool artifact = false;
            bool validShape = response.IsSuccessStatusCode && TryGetReasoningArtifact(body, out artifact);
            CompatibilityStatus status = !response.IsSuccessStatusCode || !validShape
                ? CompatibilityStatus.Failed
                : artifact ? CompatibilityStatus.Supported : CompatibilityStatus.LikelySupported;
            string detail = !response.IsSuccessStatusCode
                ? $"Reasoning 请求失败（HTTP {(int)response.StatusCode}）。"
                : !validShape
                    ? "Reasoning 返回了无法识别的 JSON 结构。"
                    : artifact
                        ? "响应包含结构化 reasoning artifact；未猜测 Codex effort 映射。"
                        : "Reasoning 模型请求成功，但响应未暴露结构化 artifact；未猜测 effort 映射。";
            return new CompatibilityResult("Reasoning", status, detail, checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CompatibilityResult("Reasoning", CompatibilityStatus.Failed, "Reasoning 阶段在完整响应读取前超时。", checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            return new CompatibilityResult("Reasoning", CompatibilityStatus.Failed, $"Reasoning 阶段协议失败（{exception.GetType().Name}）。", checkedAt);
        }
    }

    private CancellationTokenSource CreateStageTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(stageTimeout);
        return timeout;
    }

    private HttpRequestMessage CreateRequest<T>(T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, responsesPath))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        string? token = tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<bool> HasSseDataEventAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        while (buffer.Length < MaximumStreamingBodyBytes)
        {
            int remaining = MaximumStreamingBodyBytes - checked((int)buffer.Length);
            int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            string text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            if (text.Split('\n').Any(line =>
                line.TrimEnd('\r').StartsWith("data:", StringComparison.Ordinal) &&
                line.TrimEnd('\r')[5..].Trim().Length > 0))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> ReadLimitedUtf8BodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int remainingWithSentinel = maximumBytes + 1 - checked((int)buffer.Length);
            if (remainingWithSentinel <= 0)
            {
                throw new InvalidDataException($"Responses JSON 响应超过 {maximumBytes:N0} 字节上限。");
            }

            int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remainingWithSentinel)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes)
            {
                throw new InvalidDataException($"Responses JSON 响应超过 {maximumBytes:N0} 字节上限。");
            }
        }

        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
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

    private static void MarkUntested(
        List<CompatibilityResult> results,
        IEnumerable<string> capabilities,
        string detail,
        string? failureCode,
        DateTimeOffset checkedAt)
    {
        foreach (string capability in capabilities)
        {
            Upsert(results, new CompatibilityResult(capability, CompatibilityStatus.Untested, detail, checkedAt, failureCode));
        }
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

    private static void Upsert(List<CompatibilityResult> results, CompatibilityResult result)
    {
        int index = results.FindIndex(item => item.Capability.Equals(result.Capability, StringComparison.Ordinal));
        if (index < 0)
        {
            results.Add(result);
        }
        else
        {
            results[index] = result;
        }
    }

    private static void AddProbeStep(
        List<CompatibilityResult> results,
        string capability,
        CodexInstructionProbeStepResult step,
        CodexInstructionHierarchyProbeResult hierarchy,
        DateTimeOffset checkedAt)
    {
        CompatibilityStatus status = step.Passed
            ? CompatibilityStatus.Supported
            : step.HttpStatus is null ? CompatibilityStatus.Untested : CompatibilityStatus.Failed;
        string detail = step.Passed
            ? $"PASS（HTTP {step.HttpStatus}，响应包含 output 数组）。"
            : step.HttpStatus is null
                ? "前置阶段失败，未发送该请求。"
                : $"FAILED（HTTP {step.HttpStatus}）。";
        Upsert(results, new CompatibilityResult(
            capability,
            status,
            detail,
            checkedAt,
            status == CompatibilityStatus.Failed ? hierarchy.FailureCode : null));
    }

    private static object[] CreateCodexShapedInput(string userText) =>
    [
        new { role = "developer", content = "Preserve the harmless CMM compatibility-test constraints." },
        new { role = "user", content = userText },
    ];

    private static bool HasValidToolCall(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("output", out JsonElement output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "function_call" ||
                    !item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String || name.GetString() != "cmm_echo" ||
                    !item.TryGetProperty("arguments", out JsonElement arguments))
                {
                    continue;
                }

                if (ArgumentsContainExpectedValue(arguments))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool ArgumentsContainExpectedValue(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            return HasExpectedValue(arguments);
        }

        if (arguments.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string argumentJson = arguments.GetString() ?? string.Empty;
        using JsonDocument parsedArguments = JsonDocument.Parse(argumentJson);
        return parsedArguments.RootElement.ValueKind == JsonValueKind.Object && HasExpectedValue(parsedArguments.RootElement);
    }

    private static bool HasExpectedValue(JsonElement arguments) =>
        arguments.TryGetProperty("value", out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() == "CMM_TOOL_OK";

    private static bool TryGetReasoningArtifact(string json, out bool artifact)
    {
        artifact = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("output", out JsonElement output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() is string typeName && typeName.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    artifact = true;
                    return true;
                }

                if (item.TryGetProperty("reasoning_content", out JsonElement reasoningContent) &&
                    reasoningContent.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reasoningContent.GetString()))
                {
                    artifact = true;
                    return true;
                }

                if (item.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array &&
                    content.EnumerateArray().Any(part => part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("type", out JsonElement partType) &&
                        partType.ValueKind == JsonValueKind.String && partType.GetString() is string partTypeName &&
                        partTypeName.Contains("reasoning", StringComparison.OrdinalIgnoreCase)))
                {
                    artifact = true;
                    return true;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        bool validScheme = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && endpoint.IsLoopback;
        if (!endpoint.IsAbsoluteUri || !validScheme || string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("Responses endpoint 必须是无凭据、query、fragment 的 HTTPS URI，或 loopback HTTP URI。");
        }

        return endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
    }

    private static string NormalizeResponsesPath(Uri endpoint, string responsesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responsesPath);
        string normalized = responsesPath.TrimStart('/');
        if (responsesPath.StartsWith("//", StringComparison.Ordinal) ||
            responsesPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            Uri.TryCreate(responsesPath, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Responses path 必须是不能覆盖 endpoint host 的相对路径。", nameof(responsesPath));
        }

        Uri resolved = new(endpoint, normalized);
        if (!resolved.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !resolved.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != endpoint.Port)
        {
            throw new ArgumentException("Responses path 覆盖了 endpoint authority。", nameof(responsesPath));
        }

        return normalized;
    }
}
