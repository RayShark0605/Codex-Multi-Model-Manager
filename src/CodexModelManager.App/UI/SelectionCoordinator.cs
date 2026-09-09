using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

// UI-thread owned. Coalesces changes without queuing obsolete discovery work.
internal sealed class SelectionCoordinator
{
    private long revision;
    public bool Pending { get; private set; }

    public long Revision => revision;

    public void Changed() => revision++;

    public void Request()
    {
        revision++;
        Pending = true;
    }

    public bool CanPublish(long capturedRevision, ProviderKind provider, ProviderKind? currentProvider, string endpoint, string currentEndpoint) =>
        revision == capturedRevision && provider == currentProvider && string.Equals(endpoint, currentEndpoint, StringComparison.Ordinal);

    public void Applied(long capturedRevision)
    {
        if (revision == capturedRevision) Pending = false;
    }

    public static ModelProfile? Select(IReadOnlyList<ModelProfile> models, string? previousId, string? configuredId) =>
        models.FirstOrDefault(model => model.Id == previousId) ??
        models.FirstOrDefault(model => model.Id == configuredId) ??
        models.FirstOrDefault(model => model.IsLoaded == true) ?? (models.Count > 0 ? models[0] : null);
}
