using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// 切换重试预算：一次“用户显式确认的切换”全流程共享一次自动恢复机会。
/// 以 AsyncLocal 关联到异步调用链；重试前可执行校验回调确认外部状态仍支持重试。
/// </summary>
public static class SwitchRetryBudget
{
    private static readonly AsyncLocal<BudgetState?> Current = new();

    /// <summary>
    /// 开始一次已确认的切换并返回作用域：已在切换内则复用当前预算，否则新建。
    /// 释放作用域时停用新建的预算并恢复外层预算。
    /// </summary>
    public static IDisposable BeginConfirmedSwitch(Func<CancellationToken, Task>? validateBeforeRetry = null)
    {
        BudgetState? previous = Current.Value;
        BudgetState state = previous is { Active: true } ? previous : new BudgetState(validateBeforeRetry);
        Current.Value = state;
        return new Scope(previous, state, previous != state);
    }

    /// <summary>
    /// 尝试消费本次切换的唯一一次重试机会：已消费或不在切换内返回 false；
    /// 首次消费时先执行校验回调（校验抛异常即中止重试），再确认取消与激活状态。
    /// </summary>
    public static async Task<bool> TryConsumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current.Value is not { Active: true } state || Interlocked.CompareExchange(ref state.Consumed, 1, 0) != 0)
        {
            return false;
        }

        if (state.ValidateBeforeRetry is not null)
        {
            await state.ValidateBeforeRetry(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return state.Active;
    }

    /// <summary>判断异常是否属于可重试的瞬态故障（取消已请求则一律视为非瞬态）。</summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            LmStudioApiException api => IsTransientStatus(api.Failure.HttpStatus),
            HttpRequestException http => http.StatusCode is null || IsTransientStatus((int)http.StatusCode.Value),
            OperationCanceledException => true,
            _ => false,
        };
    }

    /// <summary>
    /// 判断指令层级探测结果是否属于可重试的瞬态失败：
    /// 已兼容或失败码明确指向配置/模板/实例问题时不可重试；
    /// 否则看第一处失败步骤的 HTTP 状态，或失败码是否为超时/其他 Provider 错误。
    /// </summary>
    public static bool IsTransient(CodexInstructionHierarchyProbeResult result)
    {
        if (result.IsCompatible ||
            result.FailureCode is CompatibilityFailureCodes.AuthenticationRequired or
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or
            CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder or
            CompatibilityFailureCodes.LmStudioLoadedContextChanged or
            CompatibilityFailureCodes.LmStudioLoadedInstanceMissing)
        {
            return false;
        }

        CodexInstructionProbeStepResult? failed = new[] { result.Control, result.LeadingDeveloper, result.ConversationControl, result.ContinuationDeveloper }.FirstOrDefault(step => !step.Passed);
        return failed?.HttpStatus is int status ? IsTransientStatus(status) : result.FailureCode is CompatibilityFailureCodes.Timeout or CompatibilityFailureCodes.OtherProviderError;
    }

    /// <summary>HTTP 状态码层面的瞬态判定：408/429/502/503/504。</summary>
    private static bool IsTransientStatus(int status) => status is 408 or 429 or 502 or 503 or 504;

    /// <summary>预算状态：激活标记、一次性消费标记与重试前校验回调。</summary>
    private sealed class BudgetState(Func<CancellationToken, Task>? validateBeforeRetry)
    {
        public Func<CancellationToken, Task>? ValidateBeforeRetry { get; } = validateBeforeRetry;
        public volatile bool Active = true;
        public int Consumed;
    }

    /// <summary>开始/结束切换的作用域句柄；仅在“自己新建了预算”时负责停用它。</summary>
    private sealed class Scope(BudgetState? previous, BudgetState state, bool ownsState) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (ownsState)
            {
                state.Active = false;
            }

            Current.Value = previous;
        }
    }
}
