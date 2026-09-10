using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

/// <summary>GLM Provider：模型来自所选平台的官方 catalog，凭据取自凭据提供回调，兼容性走真实 Responses 调用。</summary>
public sealed class GlmProvider : IModelProvider
{
    private readonly IGlmModelCatalogService catalog;
    private readonly GlmPlatform platform;
    private readonly Func<string?> credentialProvider;
    private readonly HttpClient httpClient;

    /// <summary>注入 catalog 服务、平台与凭据提供者构造；未提供 HttpClient 时默认 30 秒超时。</summary>
    public GlmProvider(IGlmModelCatalogService catalog, GlmPlatform platform, Func<string?> credentialProvider, HttpClient? httpClient = null)
    {
        this.catalog = catalog;
        this.platform = platform;
        this.credentialProvider = credentialProvider;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Provider 类别：GLM。</summary>
    public ProviderKind Kind => ProviderKind.GLM;

    /// <summary>探测可用性：catalog 有模型且凭据已配置。</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelProfile> models = await catalog.GetGlmModelsAsync(platform, cancellationToken).ConfigureAwait(false);
        bool credential = !string.IsNullOrWhiteSpace(credentialProvider());
        string platformName = platform == GlmPlatform.BigModel ? "智谱国内" : "国际 Z.ai";
        return new ProviderProbeResult(
            models.Count > 0 && credential,
            credential
                ? $"官方 GLM catalog 已加载（{platformName}），共 {models.Count} 个模型；凭据已配置。"
                : $"官方 GLM catalog 已加载（{platformName}）；尚未在 Windows Credential Manager 配置 GLM Token。",
            Endpoint: new Uri(GlmPlatforms.BaseUrl(platform)),
            RequiresAuthentication: !credential);
    }

    /// <summary>发现所选平台的模型清单。</summary>
    public Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default) => catalog.GetGlmModelsAsync(platform, cancellationToken);

    /// <summary>对模型执行真实 Responses 兼容性测试（按推理模型处理）。</summary>
    public Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        // 官方 base URL 不带 /v1 段；Codex 直接向 {base_url}/responses 发请求
        var tester = new ResponsesCompatibilityClient(httpClient, new Uri(GlmPlatforms.BaseUrl(platform)), credentialProvider, "responses");
        return tester.TestAsync(ProviderKind.GLM, modelId, true, cancellationToken);
    }
}
