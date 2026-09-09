using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>One recovery attempt shared by all stages of one explicitly confirmed switch.</summary>
public static class SwitchRetryBudget
{
    private static readonly AsyncLocal<BudgetState?> Current = new();

    public static IDisposable BeginConfirmedSwitch(Func<CancellationToken, Task>? validateBeforeRetry = null)
    {
        BudgetState? previous = Current.Value;
        BudgetState state = previous is { Active: true } ? previous : new BudgetState(validateBeforeRetry);
        Current.Value = state;
        return new Scope(previous, state, previous != state);
    }

    public static async Task<bool> TryConsumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current.Value is not { Active: true } state || Interlocked.CompareExchange(ref state.Consumed, 1, 0) != 0) return false;
        if (state.ValidateBeforeRetry is not null) await state.ValidateBeforeRetry(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return state.Active;
    }

    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        return exception switch
        {
            LmStudioApiException api => IsTransientStatus(api.Failure.HttpStatus),
            HttpRequestException http => http.StatusCode is null || IsTransientStatus((int)http.StatusCode.Value),
            OperationCanceledException => true,
            _ => false,
        };
    }

    public static bool IsTransient(CodexInstructionHierarchyProbeResult result)
    {
        if (result.IsCompatible || result.FailureCode is CompatibilityFailureCodes.AuthenticationRequired or
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder or CompatibilityFailureCodes.LmStudioLoadedContextChanged or
            CompatibilityFailureCodes.LmStudioLoadedInstanceMissing) return false;
        CodexInstructionProbeStepResult? failed = new[] { result.Control, result.LeadingDeveloper, result.ConversationControl, result.ContinuationDeveloper }
            .FirstOrDefault(step => !step.Passed);
        return failed?.HttpStatus is int status ? IsTransientStatus(status) :
            result.FailureCode is CompatibilityFailureCodes.Timeout or CompatibilityFailureCodes.OtherProviderError;
    }

    private static bool IsTransientStatus(int status) => status is 408 or 429 or 502 or 503 or 504;

    private sealed class BudgetState(Func<CancellationToken, Task>? validateBeforeRetry)
    {
        public Func<CancellationToken, Task>? ValidateBeforeRetry { get; } = validateBeforeRetry;
        public volatile bool Active = true;
        public int Consumed;
    }

    private sealed class Scope(BudgetState? previous, BudgetState state, bool ownsState) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ownsState) state.Active = false;
            Current.Value = previous;
        }
    }
}
