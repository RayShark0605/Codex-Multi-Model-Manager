using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

/// <summary>
/// UI 线程私有的选择协调器：以单调递增的 revision 号合并/去重模型发现请求，
/// 避免把过期的发现工作排进队列。
/// </summary>
internal sealed class SelectionCoordinator
{
    private long revision;

    /// <summary>有待处理的选择变化。</summary>
    public bool Pending { get; private set; }

    /// <summary>当前 revision 号（每次变化自增）。</summary>
    public long Revision => revision;

    /// <summary>登记一次变化（不触发请求）。</summary>
    public void Changed() => revision++;

    /// <summary>登记一次变化并标记待处理。</summary>
    public void Request()
    {
        revision++;
        Pending = true;
    }

    /// <summary>判断指定 revision 的发布是否仍有效（provider 与端点须与当前一致）。</summary>
    public bool CanPublish(long capturedRevision, ProviderKind provider, ProviderKind? currentProvider, string endpoint, string currentEndpoint) =>
        revision == capturedRevision && provider == currentProvider && string.Equals(endpoint, currentEndpoint, StringComparison.Ordinal);

    /// <summary>指定 revision 的选择已应用完毕时清除待处理标记。</summary>
    public void Applied(long capturedRevision)
    {
        if (revision == capturedRevision)
        {
            Pending = false;
        }
    }

    /// <summary>按“上次选择 → 配置值 → 已加载 → 第一个”的优先级挑选模型。</summary>
    public static ModelProfile? Select(IReadOnlyList<ModelProfile> models, string? previousId, string? configuredId) =>
        models.FirstOrDefault(model => model.Id == previousId) ??
        models.FirstOrDefault(model => model.Id == configuredId) ??
        models.FirstOrDefault(model => model.IsLoaded == true) ?? (models.Count > 0 ? models[0] : null);
}
