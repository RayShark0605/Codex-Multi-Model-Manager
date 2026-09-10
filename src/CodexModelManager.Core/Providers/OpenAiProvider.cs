using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

/// <summary>OpenAI 原生 Provider：模型与能力来自 Codex app-server（或本机缓存），不做计费冒烟测试。</summary>
public sealed class OpenAiProvider(CodexAppServerClient appServer) : IModelProvider
{
    /// <summary>Provider 类别：OpenAI。</summary>
    public ProviderKind Kind => ProviderKind.OpenAI;

    /// <summary>探测可用性：能发现模型即视为可用。</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        string? version = await appServer.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ModelProfile> models = await appServer.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderProbeResult(models.Count > 0, models.Count > 0 ? $"发现 {models.Count} 个 OpenAI/Codex 模型。" : "无法从 app-server 或 cache 发现模型。", version);
    }

    /// <summary>发现模型清单（app-server 实时优先，失败回退缓存）。</summary>
    public Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default) => appServer.ListModelsAsync(cancellationToken);

    /// <summary>基于 catalog 可见性与 provider 能力快照组装兼容性报告（不做真实调用）。</summary>
    public async Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelProfile> models = await appServer.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        bool exists = models.Any(model => model.Id == modelId);
        ProviderCapabilitySnapshot? capabilities = appServer.LastCapabilities;
        DateTimeOffset now = DateTimeOffset.Now;
        return new CompatibilityReport(ProviderKind.OpenAI, modelId,
        [
            new CompatibilityResult("Model Catalog", exists ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, exists ? "Codex app-server/catalog 可见。" : "当前 catalog 中未找到该模型。", now),
            new CompatibilityResult("Codex Agent", CompatibilityStatus.LikelySupported, "OpenAI native provider；未发起计费 smoke test。", now),
            new CompatibilityResult("Plan", CompatibilityStatus.LikelySupported, "由 Codex App 与模型 metadata 协同提供。", now),
            new CompatibilityResult("Goal", CompatibilityStatus.LikelySupported, "由 Codex App 与模型 tools 协同提供。", now),
            new CompatibilityResult("MCP", capabilities?.NamespaceTools == true ? CompatibilityStatus.LikelySupported : CompatibilityStatus.Untested, capabilities?.NamespaceTools == true ? "当前 OpenAI provider 声明 namespace tools；用户 MCP 未产生实际请求。" : "未对用户 MCP 产生请求。", now),
            new CompatibilityResult("Web Search", capabilities?.WebSearch == true ? CompatibilityStatus.Supported : CompatibilityStatus.Untested, capabilities?.WebSearch == true ? "App Server provider capability 已声明支持。" : "Provider capability 未声明或不可用。", now),
            new CompatibilityResult("Image Generation", capabilities?.ImageGeneration == true ? CompatibilityStatus.Supported : CompatibilityStatus.Untested, capabilities?.ImageGeneration == true ? "App Server provider capability 已声明支持。" : "Provider capability 未声明或不可用。", now),
        ]);
    }
}
