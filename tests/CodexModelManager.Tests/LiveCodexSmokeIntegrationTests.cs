using CodexModelManager.Core.Codex;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LiveCodexSmokeIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveCodexSmoke")]
    public async Task CurrentLoadedLmStudioModelRunsIsolatedCodexAgent()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_CODEX"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_CODEX=1 to run the live Codex smoke test.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var lm = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        ModelProfile? loaded = (await lm.DiscoverModelsAsync(TestContext.Current.CancellationToken)).FirstOrDefault(model => model.IsLoaded == true && model.ModelType == "llm" && model.LoadedContextLength is not null);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.LoadedContextLength);
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string credential = FindHelper(repository, "CodexModelManager.CredentialHelper", "credential", "CodexModelManager.CredentialHelper.exe");
        string mcp = FindHelper(repository, "CodexModelManager.TestMcpServer", "mcp", "CodexModelManager.TestMcpServer.exe");
        var smoke = new CodexSmokeTestService(credential, mcp);
        int context = loaded.LoadedContextLength.Value;
        var request = new SwitchRequest(ProviderKind.LmStudio, loaded.Id, null, context, ConfigurationSwitchService.SuggestAutoCompact(context), LmStudioProviderId: "lmstudio", LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));
        SmokeTestResult result = await smoke.RunAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Passed, result.Summary + " directory=" + result.Directory);
    }

    private static string FindHelper(string repository, string project, string publishSubdirectory, string executable)
    {
        string[] candidates =
        [
            Path.Combine(repository, "src", project, "bin", "Release", "net8.0", executable),
            Path.Combine(repository, "src", project, "bin", "Debug", "net8.0", executable),
            Path.Combine(repository, "artifacts", "publish", "win-x64", "helpers", publishSubdirectory, executable),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"测试 Helper 尚未构建: {executable}");
    }
}
