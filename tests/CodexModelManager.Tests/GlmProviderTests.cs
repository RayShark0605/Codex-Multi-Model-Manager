using System.Net;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Tests;

/// <summary>GlmProvider 相关测试集。</summary>
public sealed class GlmProviderTests
{
    [Fact]
    public async Task GlmSwitchWritesOfficialProviderTableWithCommandAuth()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM));
        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);

        Assert.Contains("model = \"glm-5.3\"", candidate, StringComparison.Ordinal);
        Assert.Contains("model_provider = \"ZAI\"", candidate, StringComparison.Ordinal);
        Assert.Contains("model_catalog_json = " + JsonSerializer.Serialize(harness.GlmCatalogPath), candidate, StringComparison.Ordinal);
        Assert.Contains("model_reasoning_effort = \"max\"", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("forced_login_method", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("model_context_window", candidate, StringComparison.Ordinal);
        Assert.Contains("[model_providers.ZAI]", candidate, StringComparison.Ordinal);
        Assert.Contains("name = \"ZAI\"", candidate, StringComparison.Ordinal);
        Assert.Contains("base_url = \"https://open.bigmodel.cn/api/v1\"", candidate, StringComparison.Ordinal);
        Assert.Contains("wire_api = \"responses\"", candidate, StringComparison.Ordinal);
        Assert.Contains("[model_providers.ZAI.auth]", candidate, StringComparison.Ordinal);
        Assert.Contains($"args = [\"{CredentialNames.Glm}\"]", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("experimental_bearer_token", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("glm-test", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlmZaiPlatformUsesInternationalBaseUrl()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest request = harness.Request(ProviderKind.GLM) with { GlmPlatform = GlmPlatform.Zai };
        SwitchPlan plan = await harness.Service.CreatePlanAsync(request);
        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);

        Assert.Contains("base_url = \"https://api.z.ai/api/v1\"", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("open.bigmodel.cn", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlmSwitchWithoutCredentialIsRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        harness.Secrets.Delete(CredentialNames.Glm);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM)));

        Assert.Contains("尚未在 Windows Credential Manager 配置 GLM Token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlmMissingCatalogMissingPlatformAndUnknownModelAreRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM) with { GlmCatalogPath = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM) with { GlmPlatform = null }));
        InvalidOperationException unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM) with { TargetModel = "glm-4.6" }));
        Assert.Contains("GLM catalog 中不存在所选模型", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlmReasoningEffortIsValidatedAgainstOfficialCatalog()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM) with { ReasoningEffort = "medium" }));
        Assert.Contains("GLM catalog 不支持 reasoning effort: medium", error.Message, StringComparison.Ordinal);

        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM) with { ReasoningEffort = "low" });
        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);
        Assert.Contains("model_reasoning_effort = \"low\"", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlmTurboWithoutReasoningLevelsKeepsDeclaredDefaultEffort()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM) with { TargetModel = "glm-5-turbo" });
        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);
        Assert.Contains("model = \"glm-5-turbo\"", candidate, StringComparison.Ordinal);
        Assert.Contains("model_reasoning_effort = \"max\"", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingOfficialGlmBearerTableIsReusedVerbatim()
    {
        const string providerTable = "[model_providers.ZAI]\n# official guide spacing\nname  =  \"ZAI\"\nbase_url = \"https://open.bigmodel.cn/api/v1\"\nwire_api = \"responses\"\nexperimental_bearer_token = \"glm-fixture-secret\"\n";
        string original = "model = \"glm-5.3\"\nmodel_provider = \"ZAI\"\n\n" + providerTable;
        using var harness = new SwitchHarness(original);

        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM));

        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(providerTable, candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("[model_providers.ZAI.auth]", candidate, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, warning => warning.Contains("GLM 官方明文 bearer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingOfficialGlmEnvironmentAwayPreservesGuideOwnedBearerTable()
    {
        const string providerTable = "[model_providers.ZAI]\n# official guide spacing\nname  =  \"ZAI\"\nbase_url = \"https://open.bigmodel.cn/api/v1\"\nwire_api = \"responses\"\nexperimental_bearer_token = \"glm-fixture-secret\"\n";
        string original = "model = \"glm-5.3\"\nmodel_provider = \"ZAI\"\nmodel_reasoning_effort = \"max\"\n\n" + providerTable;
        using var harness = new SwitchHarness(original);

        SwitchPlan openAiPlan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI));
        string openAiCandidate = Encoding.UTF8.GetString(Assert.Single(openAiPlan.Files).CandidateBytes!).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(providerTable, openAiCandidate, StringComparison.Ordinal);
        Assert.Contains("model_provider = \"openai\"", openAiCandidate, StringComparison.Ordinal);
        Assert.Contains(openAiPlan.Warnings, warning => warning.Contains("GLM 官方指南拥有", StringComparison.Ordinal));

        SwitchPlan deepSeekPlan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.DeepSeek));
        string deepSeekCandidate = Encoding.UTF8.GetString(Assert.Single(deepSeekPlan.Files).CandidateBytes!).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(providerTable, deepSeekCandidate, StringComparison.Ordinal);
        Assert.Contains("model_provider = \"deepseek\"", deepSeekCandidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagerOwnedGlmTableIsRewrittenAndRemovedOnSwitchAway()
    {
        string managerTable = "[model_providers.ZAI]\nname = \"ZAI\"\nbase_url = \"https://open.bigmodel.cn/api/v1\"\nwire_api = \"responses\"\n\n[model_providers.ZAI.auth]\ncommand = \"old-helper.exe\"\nargs = [\"CodexModelManager/GLM\"]\n";
        string original = "model = \"glm-5.3\"\nmodel_provider = \"ZAI\"\n\n" + managerTable;
        using var harness = new SwitchHarness(original);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI)));

        string switched = harness.ReadConfig();
        Assert.DoesNotContain("[model_providers.ZAI]", switched, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexModelManager/GLM", switched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlmProviderStateCapturesAndRestoresToolOutputLimit()
    {
        string initial = "model = \"glm-5.3\"\nmodel_provider = \"ZAI\"\nmodel_reasoning_effort = \"high\"\ntool_output_token_limit = 5555\n";
        using var harness = new SwitchHarness(initial);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio)));
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.GLM)));

        string restored = harness.ReadConfig();
        Assert.Contains("tool_output_token_limit = 5555", restored, StringComparison.Ordinal);
        Assert.Contains("model_reasoning_effort = \"max\"", restored, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseProviderRecognizesOfficialAndCommunityGlmIds()
    {
        Assert.Equal(ProviderKind.GLM, CodexRuntimeProbe.ParseProvider("ZAI"));
        Assert.Equal(ProviderKind.GLM, CodexRuntimeProbe.ParseProvider("glm"));
        // 枚举名会直接展示给用户（Provider 下拉框、当前 Provider、备份历史）。
        Assert.Equal("GLM", ProviderKind.GLM.ToString());
    }

    [Fact]
    public async Task GlmEmbeddedSnapshotsLoadOfflineForBothPlatforms()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        using var http = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline")));
        var service = new GlmCatalogService(home, paths, http);

        IReadOnlyList<ModelProfile> domestic = await service.GetGlmModelsAsync(GlmPlatform.BigModel);
        Assert.Contains(domestic, model => model.Id == "glm-5.3" && model.MaxContextLength == 1_048_576);
        Assert.Contains(domestic, model => model.Id == "glm-5-turbo" && model.MaxContextLength == 204_800);
        ModelProfile flagship = domestic.Single(model => model.Id == "glm-5.3");
        Assert.Null(flagship.MinimalClientVersion);
        Assert.Equal(["low", "high", "max"], flagship.ReasoningOptions);
        Assert.Equal("max", flagship.DefaultReasoningEffort);
        Assert.Equal(ProviderKind.GLM, flagship.Provider);
        Assert.True(flagship.TrainedForToolUse);

        IReadOnlyList<ModelProfile> international = await service.GetGlmModelsAsync(GlmPlatform.Zai);
        Assert.Contains(international, model => model.Id == "glm-5.3");
        Assert.DoesNotContain(international, model => model.Id == "glm-5-turbo");
    }

    [Fact]
    public async Task GlmCatalogValidationDiffersFromDeepSeekOnMinimalClientVersion()
    {
        // GLM official models.json omits minimal_client_version; it must stay optional.
        using JsonDocument valid = GlmCatalogService.ValidateCatalog(Encoding.UTF8.GetBytes("{\"models\":[{\"slug\":\"glm-x\",\"context_window\":128}]}"));
        Assert.Equal(1, valid.RootElement.GetProperty("models").GetArrayLength());
        Assert.Throws<InvalidDataException>(() => GlmCatalogService.ValidateCatalog(Encoding.UTF8.GetBytes("{\"models\":[{\"slug\":\"glm-x\",\"context_window\":128},{\"slug\":\"glm-x\",\"context_window\":128}]}")));
        Assert.Throws<InvalidDataException>(() => GlmCatalogService.ValidateCatalog(Encoding.UTF8.GetBytes("{\"models\":[{\"slug\":\"glm-x\"}]}")));
        Assert.Throws<InvalidDataException>(() => GlmCatalogService.ValidateCatalog(Encoding.UTF8.GetBytes("{\"models\":[{\"slug\":\"glm-x\",\"context_window\":128,\"minimal_client_version\":\"not-a-version\"}]}")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GlmOfficialDocsMarkdownIsDownloadedExtractedAndCached()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        string document = "# Codex\n\nsome intro\n\n```json theme={null}\n" + SwitchHarness.TestGlmCatalog.TrimEnd() + "\n```\n\n```toml\nmodel_provider = \"ZAI\"\n```\n";
        string? requestedUrl = null;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            return StubHttpHandler.Json(document, HttpStatusCode.OK);
        }));
        var service = new GlmCatalogService(home, paths, http);

        string path = await service.EnsureGlmCatalogAsync(GlmPlatform.Zai);

        Assert.Equal("https://docs.z.ai/devpack/tool/codex.md", requestedUrl);
        Assert.Equal(Path.Combine(paths.CatalogDirectory, "glm-zai-models.json"), path);
        Assert.Contains("glm-5.3", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.True(File.Exists(path + ".provenance.json"));
        Assert.False(File.Exists(Path.Combine(home.Home, "models.json")));
    }

    [Fact]
    public async Task CorruptOrForeignModelsJsonIsIgnoredForGlm()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        const string deepSeekOnly = "{\"models\":[{\"slug\":\"deepseek-v4-pro\",\"context_window\":100,\"minimal_client_version\":\"0.144.0\",\"apply_patch_tool_type\":\"freeform\",\"shell_type\":\"shell_command\",\"supported_reasoning_levels\":[]}]}";
        await File.WriteAllTextAsync(models, deepSeekOnly, new UTF8Encoding(false));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        using var http = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline")));
        var service = new GlmCatalogService(home, paths, http);

        string path = await service.EnsureGlmCatalogAsync(GlmPlatform.BigModel);

        Assert.NotEqual(models, path);
        Assert.Equal(deepSeekOnly, await File.ReadAllTextAsync(models));
        IReadOnlyList<ModelProfile> modelsFromCache = await service.GetGlmModelsAsync(GlmPlatform.BigModel);
        Assert.Contains(modelsFromCache, model => model.Id == "glm-5.3");
    }

    [Fact]
    public async Task ExistingOfficialGlmModelsJsonIsReusedWithoutRewrite()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        string models = Path.Combine(home.Home, "models.json");
        await File.WriteAllTextAsync(models, SwitchHarness.TestGlmCatalog, new UTF8Encoding(false));
        DateTime before = File.GetLastWriteTimeUtc(models);
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        using var http = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline")));
        var service = new GlmCatalogService(home, paths, http);

        Assert.Equal(models, await service.EnsureGlmCatalogAsync(GlmPlatform.BigModel));
        Assert.Equal(before, File.GetLastWriteTimeUtc(models));
    }

    [Fact]
    public async Task GlmProviderProbeReportsCredentialStatePerPlatform()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        using var http = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline")));
        var catalog = new GlmCatalogService(home, paths, http);

        var withoutCredential = new GlmProvider(catalog, GlmPlatform.BigModel, () => null, http);
        ProviderProbeResult missing = await withoutCredential.ProbeAsync();
        Assert.False(missing.IsAvailable);
        Assert.True(missing.RequiresAuthentication);
        Assert.Equal("https://open.bigmodel.cn/api/v1", missing.Endpoint!.AbsoluteUri);

        var withCredential = new GlmProvider(catalog, GlmPlatform.Zai, () => "glm-key", http);
        ProviderProbeResult present = await withCredential.ProbeAsync();
        Assert.True(present.IsAvailable);
        Assert.False(present.RequiresAuthentication);
        Assert.Equal("https://api.z.ai/api/v1", present.Endpoint!.AbsoluteUri);
    }

    [Fact]
    public async Task GlmCompatibilityClientPostsToOfficialResponsesEndpoint()
    {
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        List<Uri> requests = [];
        string? authHeader = null;
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            authHeader = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var catalog = new GlmCatalogService(home, paths, http);
        var provider = new GlmProvider(catalog, GlmPlatform.BigModel, () => "glm-key", http);

        CompatibilityReport report = await provider.TestCompatibilityAsync("glm-5.3");

        Assert.NotEmpty(requests);
        Assert.All(requests, uri => Assert.Equal(new Uri("https://open.bigmodel.cn/api/v1/responses"), uri));
        Assert.Equal("glm-key", authHeader);
        Assert.Equal(ProviderKind.GLM, report.Provider);
        Assert.Contains(report.Results, result => result.Capability == "Responses" && result.Status == CompatibilityStatus.Failed);
    }
}
