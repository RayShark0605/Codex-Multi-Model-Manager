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
        try
        {
            using HttpResponseMessage control = await SendAsync(CreateProbeBody(modelId, includeDeveloper: false), cancellationToken).ConfigureAwait(false);
            if (!control.IsSuccessStatusCode)
            {
                string body = await ReadLimitedBodyAsync(control.Content, cancellationToken).ConfigureAwait(false);
                string failureCode = ClassifyFailure(control.StatusCode, body, isControl: true);
                return new CodexInstructionHierarchyProbeResult(
                    false,
                    false,
                    (int)control.StatusCode,
                    null,
                    failureCode,
                    DescribeFailure(failureCode, (int)control.StatusCode),
                    checkedAt);
            }

            string controlBody = await ReadLimitedBodyAsync(control.Content, cancellationToken).ConfigureAwait(false);
            if (!HasOutputArray(controlBody))
            {
                return new CodexInstructionHierarchyProbeResult(
                    false,
                    false,
                    (int)control.StatusCode,
                    null,
                    CompatibilityFailureCodes.ResponsesControlFailed,
                    "普通 Responses control 返回成功状态，但响应缺少有效 output 数组。",
                    checkedAt);
            }

            using HttpResponseMessage hierarchy = await SendAsync(CreateProbeBody(modelId, includeDeveloper: true), cancellationToken).ConfigureAwait(false);
            if (hierarchy.IsSuccessStatusCode)
            {
                string successfulHierarchyBody = await ReadLimitedBodyAsync(hierarchy.Content, cancellationToken).ConfigureAwait(false);
                if (!HasOutputArray(successfulHierarchyBody))
                {
                    return new CodexInstructionHierarchyProbeResult(
                        true,
                        false,
                        (int)control.StatusCode,
                        (int)hierarchy.StatusCode,
                        CompatibilityFailureCodes.OtherProviderError,
                        "Codex-shaped Responses 返回成功状态，但响应缺少有效 output 数组。",
                        checkedAt);
                }

                return new CodexInstructionHierarchyProbeResult(
                    true,
                    true,
                    (int)control.StatusCode,
                    (int)hierarchy.StatusCode,
                    null,
                    "普通 Responses 与 Codex instructions/developer/user 指令层级请求均成功。",
                    checkedAt);
            }

            string hierarchyBody = await ReadLimitedBodyAsync(hierarchy.Content, cancellationToken).ConfigureAwait(false);
            string hierarchyFailure = ClassifyFailure(hierarchy.StatusCode, hierarchyBody, isControl: false);
            return new CodexInstructionHierarchyProbeResult(
                true,
                false,
                (int)control.StatusCode,
                (int)hierarchy.StatusCode,
                hierarchyFailure,
                DescribeFailure(hierarchyFailure, (int)hierarchy.StatusCode),
                checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CodexInstructionHierarchyProbeResult(
                false,
                false,
                null,
                null,
                CompatibilityFailureCodes.Timeout,
                "Codex 指令层级预检超时。",
                checkedAt);
        }
        catch (HttpRequestException)
        {
            return new CodexInstructionHierarchyProbeResult(
                false,
                false,
                null,
                null,
                CompatibilityFailureCodes.OtherProviderError,
                "无法连接 LM Studio Responses endpoint。",
                checkedAt);
        }
    }

    internal static object CreateProbeBody(string modelId, bool includeDeveloper)
    {
        object[] input = includeDeveloper
            ?
            [
                new { role = "developer", content = "Preserve this harmless compatibility marker: CMM_DEVELOPER_OK." },
                new { role = "user", content = "Reply with exactly CMM_ROLE_OK." },
            ]
            :
            [
                new { role = "user", content = "Reply with exactly CMM_ROLE_OK." },
            ];

        return new
        {
            model = modelId,
            instructions = "This is a harmless Codex instruction hierarchy compatibility check.",
            input,
            max_output_tokens = 32,
            stream = false,
        };
    }

    private async Task<HttpResponseMessage> SendAsync<T>(T body, CancellationToken cancellationToken)
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
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }

    private static string ClassifyFailure(HttpStatusCode statusCode, string body, bool isControl)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return CompatibilityFailureCodes.AuthenticationRequired;
        }

        if (isControl)
        {
            return CompatibilityFailureCodes.ResponsesControlFailed;
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

    private static string DescribeFailure(string failureCode, int? httpStatus) => failureCode switch
    {
        CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder =>
            "模型 Prompt Template 拒绝 Codex 的第二条 system/developer 指令；需要应用兼容模板并重载模型。",
        CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole =>
            "模型 Prompt Template 不支持 Codex developer 指令角色；需要应用兼容模板并重载模型。",
        CompatibilityFailureCodes.AuthenticationRequired =>
            "LM Studio 返回 HTTP 401，需要有效的 API Token。",
        CompatibilityFailureCodes.ResponsesControlFailed =>
            $"普通 Responses control 请求失败（HTTP {httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "未知"}）。",
        CompatibilityFailureCodes.Timeout =>
            "Codex 指令层级预检超时。",
        _ => $"Codex-shaped Responses 请求失败（HTTP {httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "未知"}）。",
    };

    private static bool HasOutputArray(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array;
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
}
