using System.Text.Json;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Tests;

public sealed class RepairConfirmationAuditTests
{
    [Fact]
    public async Task RepairPreviewWritesNothingAndCannotBypassCommitHardGate()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        string before = harness.ReadConfig();
        string[] filesBefore = Directory.GetFiles(harness.Home.Home, "*", SearchOption.AllDirectories);
        SwitchPlan preview = await harness.Service.CreateRepairPreviewAsync(harness.Request(ProviderKind.LmStudio), FakeLmStudioSwitchPreflight.Fail());
        Assert.Equal(0, harness.Preflight.CallCount);
        Assert.Equal(before, harness.ReadConfig());
        Assert.Equal(filesBefore, Directory.GetFiles(harness.Home.Home, "*", SearchOption.AllDirectories));

        harness.Preflight.DefaultResult = FakeLmStudioSwitchPreflight.Fail();
        await Assert.ThrowsAsync<LmStudioCompatibilityException>(() => harness.Service.CommitAsync(preview));
        Assert.Equal(1, harness.Preflight.CallCount);
        Assert.Equal(before, harness.ReadConfig());
    }

    [Theory]
    [InlineData(CompatibilityFailureCodes.Timeout)]
    [InlineData(CompatibilityFailureCodes.OtherProviderError)]
    public async Task UnrelatedFailureCannotCreateRepairPreview(string failureCode)
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreateRepairPreviewAsync(harness.Request(ProviderKind.LmStudio), FakeLmStudioSwitchPreflight.Fail(failureCode)));
    }

    [Fact]
    public async Task RepairConfirmationTracksSelectedExternalNoOpBeforeInstanceRebinding()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        (SwitchRequest request, string external) = await CreateSelectedNoOpExternalAsync(harness);
        string before = harness.ReadConfig();
        SwitchPlan confirmation = await harness.Service.CreateRepairPreviewAsync(request, FakeLmStudioSwitchPreflight.Fail());
        Assert.DoesNotContain(confirmation.Files, file => file.Path == external);
        Assert.NotNull(confirmation.ReadFingerprints);
        Assert.Contains(external, confirmation.ReadFingerprints.Keys);
        await ConfigurationSwitchService.VerifyConfirmationAsync(confirmation);

        const string concurrentText = "model = \"changed-by-another-process\"\n";
        await File.WriteAllTextAsync(external, concurrentText);
        SwitchPlan rebound = await harness.Service.CreatePlanAsync(request with { TargetModel = "qwen/local@q6:patched" });
        Assert.Contains(rebound.Files, file => file.Path == external);
        await Assert.ThrowsAsync<IOException>(() => ConfigurationSwitchService.VerifyConfirmationAsync(confirmation));
        Assert.Equal(before, harness.ReadConfig());
        Assert.Equal(concurrentText, await File.ReadAllTextAsync(external));
    }

    [Fact]
    public async Task UnchangedSelectedNoOpCanBecomeAReboundModelWrite()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        (SwitchRequest request, string external) = await CreateSelectedNoOpExternalAsync(harness);
        SwitchPlan confirmation = await harness.Service.CreateRepairPreviewAsync(request, FakeLmStudioSwitchPreflight.Fail());
        SwitchPlan rebound = await harness.Service.CreatePlanAsync(request with { TargetModel = "qwen/local@q6:patched" });
        await ConfigurationSwitchService.VerifyConfirmationAsync(confirmation);
        await harness.Service.CommitAsync(rebound);
        Assert.Contains("qwen/local@q6:patched", await File.ReadAllTextAsync(external), StringComparison.Ordinal);
        Assert.Equal(2, harness.Preflight.CallCount);
    }

    [Fact]
    public async Task CommitRejectsSelectedExternalNoOpDriftBeforeTheLiveProbe()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        (SwitchRequest request, string external) = await CreateSelectedNoOpExternalAsync(harness);
        SwitchPlan preview = await harness.Service.CreatePlanAsync(request);
        await File.WriteAllTextAsync(external, "model = \"concurrent\"\n");
        await Assert.ThrowsAsync<IOException>(() => harness.Service.CommitAsync(preview));
        Assert.Equal(1, harness.Preflight.CallCount);
        Assert.Empty(await harness.Backups.ListHistoryAsync());
    }

    [Fact]
    public async Task ConfirmationAndCommitRejectChangedSettingsDependencies()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan confirmation = await harness.Service.CreateRepairPreviewAsync(harness.Request(ProviderKind.LmStudio), FakeLmStudioSwitchPreflight.Fail());
        var settings = await harness.Settings.LoadAsync();
        settings.SecondaryOverrideOriginals["unrelated::model"] = "new-original";
        await harness.Settings.SaveAsync(settings);
        await Assert.ThrowsAsync<IOException>(() => ConfigurationSwitchService.VerifyConfirmationAsync(confirmation));
        await Assert.ThrowsAsync<IOException>(() => harness.Service.CommitAsync(confirmation));
        Assert.Equal(0, harness.Preflight.CallCount);
        Assert.Empty(await harness.Backups.ListHistoryAsync());
    }

    [Fact]
    public async Task LegacyPlanWithoutReadSetStillChecksItsWriteFingerprints()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan confirmation = await harness.Service.CreateRepairPreviewAsync(harness.Request(ProviderKind.LmStudio), FakeLmStudioSwitchPreflight.Fail());
        confirmation = confirmation with { ReadFingerprints = null };
        await ConfigurationSwitchService.VerifyConfirmationAsync(confirmation);
        await File.AppendAllTextAsync(Path.Combine(harness.Home.Home, "config.toml"), "\n# concurrent change\n");
        await Assert.ThrowsAsync<IOException>(() => ConfigurationSwitchService.VerifyConfirmationAsync(confirmation));
    }

    private static async Task<(SwitchRequest Request, string External)> CreateSelectedNoOpExternalAsync(SwitchHarness harness)
    {
        SwitchRequest request = harness.Request(ProviderKind.LmStudio);
        string external = Path.Combine(harness.Home.Home, "selected-worker.config.toml");
        await File.WriteAllTextAsync(external, "model = " + JsonSerializer.Serialize(request.TargetModel) + "\n");
        return (request with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain,
            SecondaryOverrideSelectionJson = JsonSerializer.Serialize(new[] { new SecondaryOverrideTarget(external, "model") }),
        }, external);
    }
}
