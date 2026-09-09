using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public sealed class GlmProvider : IModelProvider
{
    private readonly IGlmModelCatalogService catalog;
    private readonly GlmPlatform platform;
    private readonly Func<string?> credentialProvider;
    private readonly HttpClient httpClient;

    public GlmProvider(IGlmModelCatalogService catalog, GlmPlatform platform, Func<string?> credentialProvider, HttpClient? httpClient = null)
    {
        this.catalog = catalog;
        this.platform = platform;
        this.credentialProvider = credentialProvider;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public ProviderKind Kind => ProviderKind.GLM;

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

    public Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default) => catalog.GetGlmModelsAsync(platform, cancellationToken);

    public Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        // The official base URL has no trailing /v1 segment; Codex posts to {base_url}/responses.
        var tester = new ResponsesCompatibilityClient(httpClient, new Uri(GlmPlatforms.BaseUrl(platform)), credentialProvider, "responses");
        return tester.TestAsync(ProviderKind.GLM, modelId, true, cancellationToken);
    }
}
