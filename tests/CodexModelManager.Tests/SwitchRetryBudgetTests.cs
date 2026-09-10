using System.Net;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

/// <summary>SwitchRetryBudget 相关测试集。</summary>
public sealed class SwitchRetryBudgetTests
{
    [Fact]
    public async Task PreviewHasNoBudgetAndNestedConfirmedScopesShareOneValidation()
    {
        Assert.False(await SwitchRetryBudget.TryConsumeAsync());
        int validated = 0;
        using (SwitchRetryBudget.BeginConfirmedSwitch(_ => { validated++; return Task.CompletedTask; }))
        {
            using (SwitchRetryBudget.BeginConfirmedSwitch()) Assert.True(await SwitchRetryBudget.TryConsumeAsync());
            Assert.False(await SwitchRetryBudget.TryConsumeAsync());
        }
        Assert.Equal(1, validated);
        Assert.False(await SwitchRetryBudget.TryConsumeAsync());
        using (SwitchRetryBudget.BeginConfirmedSwitch()) Assert.True(await SwitchRetryBudget.TryConsumeAsync());
    }

    [Fact]
    public async Task ParallelStagesCannotConsumeMoreThanOneRetry()
    {
        using IDisposable scope = SwitchRetryBudget.BeginConfirmedSwitch();
        bool[] attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => SwitchRetryBudget.TryConsumeAsync())));
        Assert.Single(attempts, value => value);
    }

    [Fact]
    public async Task RetryGuardFailurePropagatesAndDoesNotRenewBudget()
    {
        using IDisposable scope = SwitchRetryBudget.BeginConfirmedSwitch(_ => throw new IOException("config fingerprint drift"));
        await Assert.ThrowsAsync<IOException>(() => SwitchRetryBudget.TryConsumeAsync());
        Assert.False(await SwitchRetryBudget.TryConsumeAsync());
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(500, false)]
    public void OnlySpecifiedTransientHttpFailuresCanRetry(int status, bool expected)
    {
        Assert.Equal(expected, SwitchRetryBudget.IsTransient(new HttpRequestException("failure", null, (HttpStatusCode)status)));
        Assert.Equal(expected, SwitchRetryBudget.IsTransient(new LmStudioApiException(new LmStudioApiFailure(status, null, null, null, "failure"), "failure")));
    }

    [Fact]
    public async Task CallerCancellationIsNeverRetriedOrConsumed()
    {
        using IDisposable scope = SwitchRetryBudget.BeginConfirmedSwitch();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(SwitchRetryBudget.IsTransient(new TaskCanceledException(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SwitchRetryBudget.TryConsumeAsync(cancellation.Token));
        Assert.True(await SwitchRetryBudget.TryConsumeAsync());
    }
}
