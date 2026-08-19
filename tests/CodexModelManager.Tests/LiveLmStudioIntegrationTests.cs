using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LiveLmStudioIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveLmStudio")]
    public async Task CurrentLmStudioDiscoveryAndResponsesCompatibility()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM=1 to run the live LM Studio test.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        ProviderProbeResult probe = await client.ProbeAsync(TestContext.Current.CancellationToken);
        Assert.True(probe.IsAvailable, probe.Summary);
        IReadOnlyList<ModelProfile> models = await client.DiscoverModelsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(models);
        ModelProfile? loaded = models.FirstOrDefault(model => model.IsLoaded == true && model.ModelType == "llm" && model.LoadedContextLength is not null);
        if (loaded is null)
        {
            Assert.Skip("LM Studio native API 当前未报告 loaded_instances；不把 lms ps 或理论模型列表猜作 Server 实际 loaded context。");
            return;
        }

        Assert.NotNull(loaded.LoadedContextLength);
        Assert.True(loaded.LoadedContextLength <= loaded.MaxContextLength);
        CompatibilityReport report = await client.TestCompatibilityAsync(loaded.Id, TestContext.Current.CancellationToken);
        CompatibilityResult responses = report.Results.Single(result => result.Capability == "Responses");
        CompatibilityResult hierarchy = report.Results.Single(result => result.Capability == "Codex Instruction Hierarchy");
        Assert.True(responses.Status == CompatibilityStatus.Supported, responses.Detail);
        if (hierarchy.Status == CompatibilityStatus.Failed)
        {
            Assert.Contains(hierarchy.FailureCode, new[]
            {
                CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
                CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole,
            });
            Assert.Equal(CompatibilityStatus.Failed, report.Results.Single(result => result.Capability == "Codex Agent").Status);
            return;
        }

        CompatibilityResult streaming = report.Results.Single(result => result.Capability == "Streaming");
        CompatibilityResult tools = report.Results.Single(result => result.Capability == "Tool Calling");
        Assert.True(streaming.Status == CompatibilityStatus.Supported, streaming.Detail);
        Assert.True(tools.Status == CompatibilityStatus.Supported, tools.Detail);
    }
}
