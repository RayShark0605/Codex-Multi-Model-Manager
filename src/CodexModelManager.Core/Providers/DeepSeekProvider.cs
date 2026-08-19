using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public sealed class DeepSeekProvider : IModelProvider
{
    private readonly IModelCatalogService catalog;
    private readonly Func<string?> credentialProvider;
    private readonly HttpClient httpClient;

    public DeepSeekProvider(IModelCatalogService catalog, Func<string?> credentialProvider, HttpClient? httpClient = null)
    {
        this.catalog = catalog;
        this.credentialProvider = credentialProvider;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public ProviderKind Kind => ProviderKind.DeepSeek;

    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelProfile> models = await catalog.GetDeepSeekModelsAsync(cancellationToken).ConfigureAwait(false);
        bool credential = !string.IsNullOrWhiteSpace(credentialProvider());
        return new ProviderProbeResult(models.Count > 0 && credential, credential ? $"官方 catalog 已加载，共 {models.Count} 个模型；凭据已配置。" : "官方 catalog 已加载；尚未在 Windows Credential Manager 配置 DeepSeek Token。", Endpoint: new Uri("https://api.deepseek.com/"), RequiresAuthentication: !credential);
    }

    public Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default) => catalog.GetDeepSeekModelsAsync(cancellationToken);

    public Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var tester = new ResponsesCompatibilityClient(httpClient, new Uri("https://api.deepseek.com/"), credentialProvider, "responses");
        return tester.TestAsync(ProviderKind.DeepSeek, modelId, true, cancellationToken);
    }
}
