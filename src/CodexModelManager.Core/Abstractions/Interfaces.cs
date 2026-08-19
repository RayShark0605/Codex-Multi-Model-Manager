using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Abstractions;

public interface ICodexHomeProvider
{
    string GetCodexHome();
}

public interface IModelProvider
{
    ProviderKind Kind { get; }

    Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default);

    Task<CompatibilityReport> TestCompatibilityAsync(
        string modelId,
        CancellationToken cancellationToken = default);
}

public interface IConfigPatchEngine
{
    ConfigPatchResult Apply(string originalText, ConfigPatchRequest request);

    ConfigReadResult Read(string text);

    void Validate(string text);
}

public interface IAtomicBatchWriter
{
    Task WriteAsync(
        IReadOnlyList<PlannedFileChange> changes,
        CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    string BackupRoot { get; }

    Task<string> EnsureInitialSnapshotAsync(CancellationToken cancellationToken = default);

    Task<string> CreateHistorySnapshotAsync(
        BackupOperation operation,
        string? sourceProvider,
        string? sourceModel,
        string? targetProvider,
        string? targetModel,
        IReadOnlyCollection<string>? changedKeys = null,
        IReadOnlyCollection<string>? additionalFiles = null,
        CancellationToken cancellationToken = default);

    Task EnsureSupplementalBaselinesAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupSnapshotInfo>> ListHistoryAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string snapshotDirectory, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    void Save(string targetName, ReadOnlySpan<char> secret);

    string? Read(string targetName);

    bool Exists(string targetName);

    void Delete(string targetName);
}

public interface ISecondaryModelOverrideScanner
{
    Task<IReadOnlyList<SecondaryModelOverride>> ScanAsync(
        string configPath,
        CancellationToken cancellationToken = default);
}

public interface IModelCatalogService
{
    Task<IReadOnlyList<ModelProfile>> GetDeepSeekModelsAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureDeepSeekCatalogAsync(CancellationToken cancellationToken = default);
}

public interface ICodexRuntimeProbe
{
    Task<CodexEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default);
}

public interface ICodexInstructionHierarchyProbe
{
    Task<CodexInstructionHierarchyProbeResult> ProbeAsync(
        string modelId,
        CancellationToken cancellationToken = default);
}

public interface ILmStudioSwitchPreflight
{
    Task<CodexInstructionHierarchyProbeResult> ProbeAsync(
        SwitchRequest request,
        CancellationToken cancellationToken = default);
}

public interface IGgufChatTemplateReader
{
    Task<GgufChatTemplateAnalysis> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}

public interface IPromptTemplateRepairService
{
    PromptTemplateRepairPreview CreatePreview(GgufChatTemplateAnalysis analysis);

    Task<PromptTemplateRepairArtifact> ExportAsync(
        GgufChatTemplateAnalysis analysis,
        string modelId,
        string outputRoot,
        CancellationToken cancellationToken = default);
}

public interface IAppLogger
{
    event EventHandler<string>? MessageLogged;

    void Info(string message);

    void Warning(string message);

    void LogError(string message, Exception? exception = null);
}

public sealed record ConfigPatchRequest(
    IReadOnlyDictionary<string, string?> RootValues,
    IReadOnlyDictionary<string, string?> TableBodies,
    IReadOnlyCollection<string>? RemoveTables = null);

public sealed record ConfigPatchResult(
    string Text,
    IReadOnlyList<ConfigMutation> Mutations,
    PreservationSummary Preservation);

public sealed record ConfigReadResult(
    IReadOnlyDictionary<string, string> RootValues,
    IReadOnlyDictionary<string, string> TableBodies,
    IReadOnlyList<string> Diagnostics,
    int McpServerCount,
    int ProjectCount,
    int HookSectionCount,
    int PluginSectionCount);

public sealed record BackupSnapshotInfo(
    string Directory,
    BackupManifest Manifest,
    bool HashesValid);
