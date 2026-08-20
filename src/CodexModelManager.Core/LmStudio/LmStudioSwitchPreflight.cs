using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Core.LmStudio;

public sealed class LmStudioSwitchPreflight(
    HttpClient httpClient,
    Func<string?>? tokenProvider = null) : ILmStudioSwitchPreflight
{
    public async Task<CodexInstructionHierarchyProbeResult> ProbeAsync(
        SwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TargetProvider != ProviderKind.LmStudio)
        {
            throw new ArgumentException("LM Studio preflight 只能验证 LM Studio SwitchRequest。", nameof(request));
        }

        if (request.LmStudioEndpoint is null)
        {
            throw new InvalidOperationException("LM Studio endpoint 缺失。");
        }

        LmStudioEndpointPolicy.Validate(request.LmStudioEndpoint);

        DateTimeOffset checkedAt = DateTimeOffset.Now;
        Func<string?>? effectiveTokenProvider = request.LmStudioRequiresAuthentication ? tokenProvider : null;
        try
        {
            // Re-read the authoritative native model surface before every hierarchy
            // probe. This prevents a stale Preview from using an unloaded/deleted
            // instance or a context length that changed after model reload. It also
            // avoids sending a Responses request that could cause a backend to
            // auto-load an otherwise unloaded model.
            var client = new LmStudioClient(request.LmStudioEndpoint, effectiveTokenProvider, httpClient);
            IReadOnlyList<ModelProfile> models = await client.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
            ModelProfile? loaded = models.FirstOrDefault(model =>
                model.Id.Equals(request.TargetModel, StringComparison.Ordinal) &&
                model.IsLoaded == true);
            if (loaded is null)
            {
                return Failure(
                    CompatibilityFailureCodes.LmStudioLoadedInstanceMissing,
                    "当前 LM Studio native API 未报告所选 loaded instance；未发送推理请求，也不会自动加载模型。",
                    checkedAt);
            }

            if (loaded.ModelType is not null && !loaded.ModelType.Equals("llm", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    CompatibilityFailureCodes.OtherProviderError,
                    $"当前 loaded instance 类型为 {loaded.ModelType}，不是可供 Codex 使用的 LLM。",
                    checkedAt);
            }

            if (loaded.LoadedContextLength is not int loadedContext ||
                request.ContextWindow is not int expectedContext ||
                loadedContext != expectedContext)
            {
                return Failure(
                    CompatibilityFailureCodes.LmStudioLoadedContextChanged,
                    "LM Studio 实际 loaded context 已变化或未知；请刷新模型并重新 Preview。",
                    checkedAt);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(
                CompatibilityFailureCodes.AuthenticationRequired,
                "LM Studio Models API 返回 HTTP 401，需要有效的 API Token。",
                checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                CompatibilityFailureCodes.Timeout,
                "LM Studio loaded instance 实时检查超时。",
                checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or AggregateException)
        {
            return Failure(
                CompatibilityFailureCodes.OtherProviderError,
                "无法从 LM Studio native Models API 重新确认 loaded instance。",
                checkedAt);
        }

        var probe = new CodexInstructionHierarchyProbe(
            httpClient,
            request.LmStudioEndpoint,
            effectiveTokenProvider);
        return await probe.ProbeAsync(request.TargetModel, cancellationToken).ConfigureAwait(false);
    }

    private static CodexInstructionHierarchyProbeResult Failure(string code, string detail, DateTimeOffset checkedAt) => new(
        new CodexInstructionProbeStepResult(false, null),
        new CodexInstructionProbeStepResult(false, null),
        new CodexInstructionProbeStepResult(false, null),
        new CodexInstructionProbeStepResult(false, null),
        code,
        detail,
        checkedAt);
}

public sealed class LmStudioCompatibilityException : InvalidOperationException
{
    public LmStudioCompatibilityException(CodexInstructionHierarchyProbeResult result)
        : base($"LM Studio Codex 指令层级预检失败 [{result.FailureCode ?? CompatibilityFailureCodes.OtherProviderError}]：{result.Detail}")
    {
        Result = result;
    }

    public CodexInstructionHierarchyProbeResult Result { get; }
}
