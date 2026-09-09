using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
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
        string? baseline = null;
        bool AcceptSnapshot(ModelProfile model)
        {
            string fingerprint = JsonSerializer.Serialize(new { model.Id, model.SourceModelKey, model.ModelType, model.Architecture, model.Quantization, model.Parameters, model.SelectedVariant, model.MaxContextLength, model.LoadedConfiguration, model.Format });
            baseline ??= fingerprint;
            return baseline.Equals(fingerprint, StringComparison.Ordinal);
        }

        CodexInstructionHierarchyProbeResult result = await ProbeOnceAsync(request, AcceptSnapshot, cancellationToken).ConfigureAwait(false);
        if (SwitchRetryBudget.IsTransient(result) && await SwitchRetryBudget.TryConsumeAsync(cancellationToken).ConfigureAwait(false))
        {
            result = await ProbeOnceAsync(request, AcceptSnapshot, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<CodexInstructionHierarchyProbeResult> ProbeOnceAsync(
        SwitchRequest request,
        Func<ModelProfile, bool> acceptSnapshot,
        CancellationToken cancellationToken)
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
            IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
            ModelProfile? loaded = models.SingleOrDefault(model =>
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
                    "lmstudio-non-llm-instance",
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

            if (!acceptSnapshot(loaded))
            {
                return Failure(CompatibilityFailureCodes.LmStudioLoadedContextChanged, "LM Studio 实例身份或加载配置已漂移；未重试推理。", checkedAt);
            }

            var probe = new CodexInstructionHierarchyProbe(httpClient, request.LmStudioEndpoint, effectiveTokenProvider);
            CodexInstructionHierarchyProbeResult hierarchy = await probe.ProbeAsync(request.TargetModel, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ModelProfile> after = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
            ModelProfile? current = after.SingleOrDefault(model => model.Id.Equals(request.TargetModel, StringComparison.Ordinal) && model.IsLoaded == true);
            if (current is null) return Failure(CompatibilityFailureCodes.LmStudioLoadedInstanceMissing, "四阶段检测后所选 native loaded instance 已不存在。", checkedAt);
            if (!acceptSnapshot(current)) return Failure(CompatibilityFailureCodes.LmStudioLoadedContextChanged, "四阶段检测期间实例身份或完整加载配置发生变化。", checkedAt);
            return hierarchy;
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
        catch (HttpRequestException exception)
        {
            return Failure(SwitchRetryBudget.IsTransient(exception, cancellationToken) ? CompatibilityFailureCodes.OtherProviderError : "lmstudio-native-state-unavailable",
                "无法从 LM Studio native Models API 重新确认 loaded instance。", checkedAt);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or AggregateException)
        {
            return Failure(
                "lmstudio-native-state-invalid",
                "无法从 LM Studio native Models API 重新确认 loaded instance。",
                checkedAt);
        }

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
