using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

/// <summary>DeepSeek Provider：模型来自官方 catalog 缓存，凭据取自凭据提供回调，兼容性走真实 Responses 调用。</summary>
public sealed class DeepSeekProvider : IModelProvider
{
    private readonly IModelCatalogService catalog;
    private readonly Func<string?> credentialProvider;
    private readonly HttpClient httpClient;

    /// <summary>注入 catalog 服务与凭据提供者构造；未提供 HttpClient 时默认 30 秒超时。</summary>
    public DeepSeekProvider(IModelCatalogService catalog, Func<string?> credentialProvider, HttpClient? httpClient = null)
    {
        this.catalog = catalog;
        this.credentialProvider = credentialProvider;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Provider 类别：DeepSeek。</summary>
    public ProviderKind Kind => ProviderKind.DeepSeek;

    /// <summary>探测可用性：catalog 有模型且凭据已配置。</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelProfile> models = await catalog.GetDeepSeekModelsAsync(cancellationToken).ConfigureAwait(false);
        bool credential = !string.IsNullOrWhiteSpace(credentialProvider());
        return new ProviderProbeResult(models.Count > 0 && credential, credential ? $"官方 catalog 已加载，共 {models.Count} 个模型；凭据已配置。" : "官方 catalog 已加载；尚未在 Windows Credential Manager 配置 DeepSeek Token。", Endpoint: new Uri("https://api.deepseek.com/"), RequiresAuthentication: !credential);
    }

    /// <summary>发现模型清单（官方 catalog）。</summary>
    public Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default) => catalog.GetDeepSeekModelsAsync(cancellationToken);

    /// <summary>对模型执行真实 Responses 兼容性测试（按推理模型处理）。</summary>
    public Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var tester = new ResponsesCompatibilityClient(httpClient, new Uri("https://api.deepseek.com/"), credentialProvider, "responses");
        return tester.TestAsync(ProviderKind.DeepSeek, modelId, true, cancellationToken);
    }
}
