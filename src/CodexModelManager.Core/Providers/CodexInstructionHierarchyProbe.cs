using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public sealed class CodexInstructionHierarchyProbe : ICodexInstructionHierarchyProbe
{
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private static readonly CodexInstructionProbeStepResult NotRun = new(false, null);
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly Func<string?>? tokenProvider;
    private readonly string responsesPath;
    private readonly TimeSpan requestTimeout;

    public CodexInstructionHierarchyProbe(
        HttpClient httpClient,
        Uri endpoint,
        Func<string?>? tokenProvider = null,
        string responsesPath = "v1/responses",
        TimeSpan? requestTimeout = null)
    {
        LmStudioEndpointPolicy.Validate(endpoint);
        if (Uri.TryCreate(responsesPath, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Responses path 必须是相对路径，不能覆盖 LM Studio endpoint。", nameof(responsesPath));
        }

        this.httpClient = httpClient;
        this.endpoint = endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/");
        this.tokenProvider = tokenProvider;
        this.responsesPath = responsesPath.TrimStart('/');
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(45);
    }

    public async Task<CodexInstructionHierarchyProbeResult> ProbeAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        DateTimeOffset checkedAt = DateTimeOffset.Now;
        CodexInstructionProbeStepResult control = NotRun;
        CodexInstructionProbeStepResult leadingDeveloper = NotRun;
        CodexInstructionProbeStepResult conversationControl = NotRun;
        CodexInstructionProbeStepResult continuationDeveloper = NotRun;

        try
        {
            ProbeHttpResult controlHttp = await SendAsync(CreateProbeBody(modelId, ProbeShape.Control), cancellationToken).ConfigureAwait(false);
            control = controlHttp.Step;
            if (!control.Passed)
            {
                string failureCode = ClassifyFailure(controlHttp.StatusCode, controlHttp.Body, ProbeShape.Control);
                return Result(control, leadingDeveloper, conversationControl, continuationDeveloper, failureCode, DescribeFailure(failureCode, control.HttpStatus, ProbeShape.Control), checkedAt);
            }

            ProbeHttpResult leadingHttp = await SendAsync(CreateProbeBody(modelId, ProbeShape.LeadingDeveloper), cancellationToken).ConfigureAwait(false);
            leadingDeveloper = leadingHttp.Step;
            if (!leadingDeveloper.Passed)
            {
                string failureCode = ClassifyFailure(leadingHttp.StatusCode, leadingHttp.Body, ProbeShape.LeadingDeveloper);
                return Result(control, leadingDeveloper, conversationControl, continuationDeveloper, failureCode, DescribeFailure(failureCode, leadingDeveloper.HttpStatus, ProbeShape.LeadingDeveloper), checkedAt);
            }

            ProbeHttpResult conversationHttp = await SendAsync(CreateProbeBody(modelId, ProbeShape.ConversationControl), cancellationToken).ConfigureAwait(false);
            conversationControl = conversationHttp.Step;
            if (!conversationControl.Passed)
            {
                string failureCode = ClassifyFailure(conversationHttp.StatusCode, conversationHttp.Body, ProbeShape.ConversationControl);
                return Result(control, leadingDeveloper, conversationControl, continuationDeveloper, failureCode, DescribeFailure(failureCode, conversationControl.HttpStatus, ProbeShape.ConversationControl), checkedAt);
            }

            ProbeHttpResult continuationHttp = await SendAsync(CreateProbeBody(modelId, ProbeShape.ContinuationDeveloper), cancellationToken).ConfigureAwait(false);
            continuationDeveloper = continuationHttp.Step;
            if (!continuationDeveloper.Passed)
            {
                string failureCode = ClassifyFailure(continuationHttp.StatusCode, continuationHttp.Body, ProbeShape.ContinuationDeveloper);
                return Result(control, leadingDeveloper, conversationControl, continuationDeveloper, failureCode, DescribeFailure(failureCode, continuationDeveloper.HttpStatus, ProbeShape.ContinuationDeveloper), checkedAt);
            }

            return Result(
                control,
                leadingDeveloper,
                conversationControl,
                continuationDeveloper,
                null,
                "普通 Responses、前导 developer、多轮 conversation control 与后置 developer 请求均成功。",
                checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(control, leadingDeveloper, conversationControl, continuationDeveloper, CompatibilityFailureCodes.Timeout, "Codex 指令层级预检超时。", checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Result(control, leadingDeveloper, conversationControl, continuationDeveloper, CompatibilityFailureCodes.OtherProviderError, "无法连接 LM Studio Responses endpoint。", checkedAt);
        }
    }

    internal static object CreateProbeBody(string modelId, ProbeShape shape)
    {
        object[] input = shape switch
        {
            ProbeShape.Control =>
            [
                new { role = "user", content = "Reply with exactly CMM_ROLE_OK." },
            ],
            ProbeShape.LeadingDeveloper =>
            [
                new { role = "developer", content = "Preserve this harmless compatibility marker: CMM_DEVELOPER_OK." },
                new { role = "user", content = "Reply with exactly CMM_ROLE_OK." },
            ],
            ProbeShape.ConversationControl =>
            [
                new { role = "developer", content = "Preserve this harmless compatibility marker: CMM_DEVELOPER_OK." },
                new { role = "user", content = "First harmless conversation turn." },
                new { role = "assistant", content = "First harmless conversation turn acknowledged." },
                new { role = "user", content = "Reply with exactly CMM_ROLE_OK." },
            ],
            ProbeShape.ContinuationDeveloper =>
            [
                new { role = "developer", content = "Preserve this harmless compatibility marker: CMM_DEVELOPER_OK." },
                new { role = "user", content = "First harmless conversation turn." },
                new { role = "assistant", content = "First harmless conversation turn acknowledged." },
                new { role = "developer", content = "Apply this harmless continuation marker: CMM_CONTINUATION_OK." },
                new { role = "user", content = "Reply with exactly CMM_ROLE_OK." },
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        return new
        {
            model = modelId,
            instructions = "This is a harmless Codex instruction hierarchy compatibility check.",
            input,
            max_output_tokens = 32,
            stream = false,
        };
    }

    private async Task<ProbeHttpResult> SendAsync<T>(T body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, responsesPath))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        string? token = tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        string bodyText = await ReadLimitedBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
        bool passed = response.IsSuccessStatusCode && HasOutputArray(bodyText);
        return new ProbeHttpResult(new CodexInstructionProbeStepResult(passed, (int)response.StatusCode), response.StatusCode, bodyText);
    }

    private static CodexInstructionHierarchyProbeResult Result(
        CodexInstructionProbeStepResult control,
        CodexInstructionProbeStepResult leadingDeveloper,
        CodexInstructionProbeStepResult conversationControl,
        CodexInstructionProbeStepResult continuationDeveloper,
        string? failureCode,
        string detail,
        DateTimeOffset checkedAt) => new(
            control,
            leadingDeveloper,
            conversationControl,
            continuationDeveloper,
            failureCode,
            detail,
            checkedAt);

    private static string ClassifyFailure(HttpStatusCode statusCode, string body, ProbeShape shape)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return CompatibilityFailureCodes.AuthenticationRequired;
        }

        if (shape == ProbeShape.Control)
        {
            return CompatibilityFailureCodes.ResponsesControlFailed;
        }

        if (shape == ProbeShape.ConversationControl)
        {
            return CompatibilityFailureCodes.ResponsesConversationControlFailed;
        }

        if (shape == ProbeShape.ContinuationDeveloper &&
            body.Contains("System and developer messages must precede conversation messages.", StringComparison.OrdinalIgnoreCase))
        {
            return CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder;
        }

        if (body.Contains("System message must be at the beginning", StringComparison.OrdinalIgnoreCase))
        {
            return CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder;
        }

        if (body.Contains("Unexpected message role", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("developer role", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("role 'developer'", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("role \"developer\"", StringComparison.OrdinalIgnoreCase))
        {
            return CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
        }

        return CompatibilityFailureCodes.OtherProviderError;
    }

    private static string DescribeFailure(
        string failureCode,
        int? httpStatus,
        ProbeShape shape) => failureCode switch
        {
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder when shape == ProbeShape.LeadingDeveloper =>
                "模型 Prompt Template 拒绝前导独立 developer 指令；需要应用兼容模板并重载模型。",
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder when shape == ProbeShape.ContinuationDeveloper =>
                "模型 Prompt Template 只接受开头连续的 system/developer 指令，拒绝多轮对话中的后置 developer；需要应用兼容模板并重载模型。",
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder =>
                "模型 Prompt Template 拒绝 Codex 的 system/developer 指令顺序；需要应用兼容模板并重载模型。",
            CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole =>
                "模型 Prompt Template 不支持 Codex developer 指令角色；需要应用兼容模板并重载模型。",
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder =>
                "旧版运行时 Prompt Template 拒绝多轮对话中的后置 developer 指令；需要升级为 interleaved-instructions v3。",
            CompatibilityFailureCodes.AuthenticationRequired =>
                "LM Studio 返回 HTTP 401，需要有效的 API Token。",
            CompatibilityFailureCodes.ResponsesControlFailed =>
                $"普通 Responses control 请求失败（HTTP {FormatStatus(httpStatus)}）。",
            CompatibilityFailureCodes.ResponsesConversationControlFailed =>
                $"不含后置 developer 的多轮 conversation control 请求失败（HTTP {FormatStatus(httpStatus)}）；该错误不允许自动套用模板修补。",
            CompatibilityFailureCodes.Timeout =>
                "Codex 指令层级预检超时。",
            _ => $"Codex-shaped Responses 请求失败（HTTP {FormatStatus(httpStatus)}）。",
        };

    private static string FormatStatus(int? status) => status?.ToString(CultureInfo.InvariantCulture) ?? "未知";

    private static bool HasOutputArray(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("output", out JsonElement output) &&
                output.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> ReadLimitedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        while (buffer.Length < MaximumErrorBodyBytes)
        {
            int remaining = MaximumErrorBodyBytes - checked((int)buffer.Length);
            int read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    internal enum ProbeShape
    {
        Control,
        LeadingDeveloper,
        ConversationControl,
        ContinuationDeveloper
    }

    private sealed record ProbeHttpResult(
        CodexInstructionProbeStepResult Step,
        HttpStatusCode StatusCode,
        string Body);
}
