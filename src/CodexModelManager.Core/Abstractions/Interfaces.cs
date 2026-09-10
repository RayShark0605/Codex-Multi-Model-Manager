using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Abstractions;

/// <summary>Codex 主目录（CODEX_HOME）解析抽象。</summary>
public interface ICodexHomeProvider
{
    /// <summary>返回 Codex 主目录的绝对路径。</summary>
    string GetCodexHome();
}

/// <summary>模型 Provider 抽象：探测可用性、发现模型列表并做兼容性测试。</summary>
public interface IModelProvider
{
    /// <summary>Provider 类别。</summary>
    ProviderKind Kind { get; }

    /// <summary>探测服务可用性（凭据、端点连通性等）。</summary>
    Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>发现该 Provider 提供的模型清单。</summary>
    Task<IReadOnlyList<ModelProfile>> DiscoverModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>对指定模型执行兼容性测试（真实调用并产出报告）。</summary>
    Task<CompatibilityReport> TestCompatibilityAsync(string modelId, CancellationToken cancellationToken = default);
}

/// <summary>配置补丁引擎抽象：对 Codex config.toml 文本做结构化读、写与校验。</summary>
public interface IConfigPatchEngine
{
    /// <summary>把补丁请求应用到原始文本，返回新文本与变更/保留明细。</summary>
    ConfigPatchResult Apply(string originalText, ConfigPatchRequest request);

    /// <summary>结构化读取文本：根键值、表体与诊断信息。</summary>
    ConfigReadResult Read(string text);

    /// <summary>校验文本是否为可解析的合法 TOML，不合法即抛异常。</summary>
    void Validate(string text);
}

/// <summary>原子批量写入器抽象：把一组文件变更作为单事务提交（全成或全回滚）。</summary>
public interface IAtomicBatchWriter
{
    /// <summary>执行批量写入事务。</summary>
    Task WriteAsync(IReadOnlyList<PlannedFileChange> changes, CancellationToken cancellationToken = default);
}

/// <summary>配置备份服务抽象：初始快照、历史快照、补充基线、列举与恢复。</summary>
public interface IBackupService
{
    /// <summary>备份根目录。</summary>
    string BackupRoot { get; }

    /// <summary>确保初始基线快照存在（首次运行时创建），返回快照目录。</summary>
    Task<string> EnsureInitialSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>创建一次历史快照，记录触发本次备份的 Provider/模型切换上下文。</summary>
    Task<string> CreateHistorySnapshotAsync(
        BackupOperation operation,
        string? sourceProvider,
        string? sourceModel,
        string? targetProvider,
        string? targetModel,
        IReadOnlyCollection<string>? changedKeys = null,
        IReadOnlyCollection<string>? additionalFiles = null,
        CancellationToken cancellationToken = default);

    /// <summary>为初次纳入备份的文件补充基线快照。</summary>
    Task EnsureSupplementalBaselinesAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default);

    /// <summary>按时间列出全部历史快照。</summary>
    Task<IReadOnlyList<BackupSnapshotInfo>> ListHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>从指定快照目录恢复配置。</summary>
    Task RestoreAsync(string snapshotDirectory, CancellationToken cancellationToken = default);
}

/// <summary>密钥存取抽象（如 Windows 凭据管理器）。</summary>
public interface ISecretStore
{
    /// <summary>保存（覆盖）密钥。</summary>
    void Save(string targetName, ReadOnlySpan<char> secret);

    /// <summary>读取密钥；不存在返回 null。</summary>
    string? Read(string targetName);

    /// <summary>判断密钥是否存在。</summary>
    bool Exists(string targetName);

    /// <summary>删除密钥。</summary>
    void Delete(string targetName);
}

/// <summary>Secondary Model Override 扫描抽象：从 Codex 配置中找出二级模型覆盖项。</summary>
public interface ISecondaryModelOverrideScanner
{
    /// <summary>扫描指定配置文件并返回全部覆盖项。</summary>
    Task<IReadOnlyList<SecondaryModelOverride>> ScanAsync(string configPath, CancellationToken cancellationToken = default);
}

/// <summary>DeepSeek 模型目录服务抽象。</summary>
public interface IModelCatalogService
{
    /// <summary>获取 DeepSeek 模型清单（优先用缓存目录，必要时抓取）。</summary>
    Task<IReadOnlyList<ModelProfile>> GetDeepSeekModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>确保 DeepSeek 模型目录缓存文件就绪，返回其路径。</summary>
    Task<string> EnsureDeepSeekCatalogAsync(CancellationToken cancellationToken = default);
}

/// <summary>GLM 模型目录服务抽象（区分国内与 Coding Plan 国际两个平台）。</summary>
public interface IGlmModelCatalogService
{
    /// <summary>获取指定平台的 GLM 模型清单。</summary>
    Task<IReadOnlyList<ModelProfile>> GetGlmModelsAsync(GlmPlatform platform, CancellationToken cancellationToken = default);

    /// <summary>确保指定平台的 GLM 模型目录缓存就绪，返回其路径。</summary>
    Task<string> EnsureGlmCatalogAsync(GlmPlatform platform, CancellationToken cancellationToken = default);
}

/// <summary>Codex 运行环境探测抽象：定位 CLI、检测运行中的进程等。</summary>
public interface ICodexRuntimeProbe
{
    /// <summary>探测当前环境的 Codex 安装与运行状态。</summary>
    Task<CodexEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Codex 指令层级探测抽象：验证模型的指令遵循层级是否满足切换要求。</summary>
public interface ICodexInstructionHierarchyProbe
{
    /// <summary>对指定模型执行指令层级探测。</summary>
    Task<CodexInstructionHierarchyProbeResult> ProbeAsync(string modelId, CancellationToken cancellationToken = default);
}

/// <summary>LM Studio 切换前置检查抽象：切换前验证目标模型的指令层级。</summary>
public interface ILmStudioSwitchPreflight
{
    /// <summary>对切换请求做前置探测。</summary>
    Task<CodexInstructionHierarchyProbeResult> ProbeAsync(SwitchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>GGUF 聊天模板读取抽象：解析模型文件内嵌的提示词模板。</summary>
public interface IGgufChatTemplateReader
{
    /// <summary>读取并分析指定模型文件的聊天模板。</summary>
    Task<GgufChatTemplateAnalysis> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>LM Studio 模型文件定位抽象：把模型档案解析为磁盘上的 GGUF 文件。</summary>
public interface ILmStudioModelFileLocator
{
    /// <summary>解析模型文件位置，返回带诊断信息的尝试结果。</summary>
    Task<LmStudioModelFileResolutionAttempt> ResolveAsync(ModelProfile model, Uri endpoint, CancellationToken cancellationToken = default);
}

/// <summary>提示词模板修复服务抽象：预览、重建已知模板并导出修复工件。</summary>
public interface IPromptTemplateRepairService
{
    /// <summary>为一份模板分析结果创建修复预览（不落盘）。</summary>
    PromptTemplateRepairPreview CreatePreview(GgufChatTemplateAnalysis analysis);

    /// <summary>按规则版本与期望哈希重建已知的正确模板文本。</summary>
    string RecreateKnownTemplate(GgufChatTemplateAnalysis analysis, string ruleVersion, string expectedTemplateSha256);

    /// <summary>导出修复工件（分析、模板与元数据）到输出目录。</summary>
    Task<PromptTemplateRepairArtifact> ExportAsync(GgufChatTemplateAnalysis analysis, string modelId, string outputRoot, CancellationToken cancellationToken = default);
}

/// <summary>LM Studio 实例控制器抽象：捕获实例状态、应用/回滚模板修复与事务恢复。</summary>
public interface ILmStudioInstanceController : IDisposable
{
    /// <summary>捕获指定实例的当前状态快照。</summary>
    Task<LmStudioLoadedInstanceSnapshot> CaptureAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>按计划对实例应用模板修复。</summary>
    Task<LmStudioTemplateRepairResult> ApplyTemplateAsync(LmStudioTemplateRepairPlan plan, CancellationToken cancellationToken = default);

    /// <summary>回滚一次模板修复（可指定修复后的实例 ID）。</summary>
    Task<LmStudioRollbackResult> RollbackAsync(LmStudioTemplateRepairPlan plan, string? patchedInstanceId, CancellationToken cancellationToken = default);

    /// <summary>依据事务记录自动评估并执行恢复。</summary>
    Task<LmStudioRollbackResult> RecoverAsync(LmStudioTemplateTransactionRecord transaction, CancellationToken cancellationToken = default);

    /// <summary>评估事务的恢复策略（不实际执行恢复）。</summary>
    Task<LmStudioRecoveryAssessment> AssessRecoveryAsync(LmStudioTemplateTransactionRecord transaction, CancellationToken cancellationToken = default);

    /// <summary>依据已有评估结果执行恢复。</summary>
    Task<LmStudioRollbackResult> RecoverAsync(LmStudioTemplateTransactionRecord transaction, LmStudioRecoveryAssessment assessment, CancellationToken cancellationToken = default);

    /// <summary>标记事务完成（清理进行中标记）。</summary>
    Task CompleteAsync(Guid transactionId, CancellationToken cancellationToken = default);
}

/// <summary>应用日志抽象。</summary>
public interface IAppLogger
{
    /// <summary>每写入一条日志后触发。</summary>
    event EventHandler<string>? MessageLogged;

    /// <summary>记录 INFO 级别日志。</summary>
    void Info(string message);

    /// <summary>记录 WARN 级别日志。</summary>
    void Warning(string message);

    /// <summary>记录 ERROR 级别日志（可附异常）。</summary>
    void LogError(string message, Exception? exception = null);
}

/// <summary>配置补丁请求：根键值覆盖、表体替换与待删除表。</summary>
public sealed record ConfigPatchRequest(
    IReadOnlyDictionary<string, string?> RootValues,
    IReadOnlyDictionary<string, string?> TableBodies,
    IReadOnlyCollection<string>? RemoveTables = null);

/// <summary>配置补丁结果：新文本、实际执行的变更列表与保留摘要。</summary>
public sealed record ConfigPatchResult(
    string Text,
    IReadOnlyList<ConfigMutation> Mutations,
    PreservationSummary Preservation);

/// <summary>配置结构化读取结果：根键值、表体、诊断与各节计数。</summary>
public sealed record ConfigReadResult(
    IReadOnlyDictionary<string, string> RootValues,
    IReadOnlyDictionary<string, string> TableBodies,
    IReadOnlyList<string> Diagnostics,
    int McpServerCount,
    int ProjectCount,
    int HookSectionCount,
    int PluginSectionCount);

/// <summary>备份快照信息：目录、清单与哈希校验结果。</summary>
public sealed record BackupSnapshotInfo(
    string Directory,
    BackupManifest Manifest,
    bool HashesValid);
