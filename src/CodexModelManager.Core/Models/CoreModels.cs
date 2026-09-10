using System.Text.Json.Serialization;

namespace CodexModelManager.Core.Models;

/// <summary>Provider 类别。枚举名会直接展示在界面（Provider 下拉框、当前 Provider、备份历史）。</summary>
public enum ProviderKind
{
    Unknown,
    OpenAI,
    DeepSeek,
    LmStudio,
    // 全大写成员：该名称面向用户展示，而产品官方写法固定为“GLM”
    GLM
}

/// <summary>GLM Coding Plan 的服务平台。两个官方平台共用同一 Provider 表 ID，但基础 URL 与官方模型目录不同。</summary>
public enum GlmPlatform
{
    /// <summary>智谱国内平台（bigmodel.cn）。</summary>
    BigModel,
    /// <summary>Coding Plan 国际平台（z.ai）。</summary>
    Zai
}

/// <summary>单项能力兼容性结论。</summary>
public enum CompatibilityStatus
{
    Supported,
    LikelySupported,
    Untested,
    KnownLimitation,
    Failed
}

/// <summary>提示词模板修复的可行性状态。</summary>
public enum PromptTemplateRepairStatus
{
    Supported,
    UpgradeRequired,
    AlreadyCompatible,
    Unsupported
}

/// <summary>运行时模板来源：LM Studio 内建，还是本管理器注入的规则模板。</summary>
public enum LmStudioRuntimeTemplateMode
{
    BuiltIn,
    ManagerRule
}

/// <summary>每模型默认值（per-model defaults）持久化状态机的取值。</summary>
public enum LmStudioPersistenceStatus
{
    BuiltInNoOverride,
    LegacyRuntimeOnlyPatch,
    PersistentV3Applied,
    PersistentV2UpgradeRequired,
    PersistentOverrideMissingAfterReload,
    UnsupportedCustomOverride,
    UnsupportedLmStudioVersion,
    PersistenceStateAmbiguous
}

/// <summary>持久化模板字段的当前版本：缺失 / 旧版 v2 / 现行 v3。</summary>
public enum LmStudioPersistentTemplateFieldState
{
    Missing,
    ManagerV2,
    ManagerV3
}

/// <summary>每模型默认值需要的变更类型。</summary>
public enum LmStudioPerModelDefaultsMutation
{
    Add,
    Upgrade,
    NoOp
}

/// <summary>模板事务持久化阶段的推进状态。</summary>
public enum LmStudioPersistenceStage
{
    None,
    Prepared,
    BackupVerified,
    DefaultsVerified,
    PersistentDefaultVerified,
    Restored,
    RecoveryBlocked
}

/// <summary>模板事务状态机的全部状态。</summary>
public enum LmStudioTemplateTransactionState
{
    Prepared,
    OriginalUnloaded,
    PatchedLoaded,
    PatchedAndVerified,
    RolledBack,
    RollbackFailed,
    RecoveryBlocked,
    Completed
}

/// <summary>模板事务生命周期各阶段（用于失败定位与恢复决策）。</summary>
public enum LmStudioLifecycleStage
{
    None,
    ApplyPreflight,
    PersistDefaults,
    UnloadOriginal,
    LoadPatched,
    ValidatePatched,
    ProbePatched,
    UnloadPatched,
    LoadOriginal,
    ValidateOriginal,
    ProbeOriginal,
    RestoreDefaults,
    RecoveryAssessment,
    RecoveryCommit
}

/// <summary>事务恢复的处置结论。</summary>
public enum LmStudioRecoveryDisposition
{
    AlreadyRestored,
    LoadOriginal,
    UnloadKnownPatchAndLoadOriginal,
    BlockedAmbiguous
}

/// <summary>兼容性失败码常量表（写入 CompatibilityResult.FailureCode，供测试与诊断匹配）。</summary>
public static class CompatibilityFailureCodes
{
    /// <summary>聊天模板 system 消息顺序不符合要求。</summary>
    public const string LmStudioChatTemplateSystemOrder = "lmstudio-chat-template-system-order";
    /// <summary>聊天模板缺少 developer 角色支持。</summary>
    public const string LmStudioChatTemplateDeveloperRole = "lmstudio-chat-template-developer-role";
    /// <summary>续轮指令顺序不符合要求。</summary>
    public const string LmStudioChatTemplateContinuationInstructionOrder = "lmstudio-chat-template-continuation-instruction-order";
    /// <summary>Responses 控制调用失败。</summary>
    public const string ResponsesControlFailed = "responses-control-failed";
    /// <summary>Responses 会话控制调用失败。</summary>
    public const string ResponsesConversationControlFailed = "responses-conversation-control-failed";
    /// <summary>需要认证（凭据缺失或失效）。</summary>
    public const string AuthenticationRequired = "authentication-required";
    /// <summary>请求超时。</summary>
    public const string Timeout = "timeout";
    /// <summary>其他 Provider 侧错误。</summary>
    public const string OtherProviderError = "other-provider-error";
    /// <summary>找不到已加载的 LM Studio 实例。</summary>
    public const string LmStudioLoadedInstanceMissing = "lmstudio-loaded-instance-missing";
    /// <summary>已加载实例的上下文发生变化。</summary>
    public const string LmStudioLoadedContextChanged = "lmstudio-loaded-context-changed";
    /// <summary>已加载实例在操作期间被替换。</summary>
    public const string LmStudioLoadedInstanceChanged = "lmstudio-loaded-instance-changed";
    /// <summary>LM Studio 生命周期操作失败。</summary>
    public const string LmStudioLifecycleFailed = "lmstudio-lifecycle-failed";
}

/// <summary>模型文件定位的结果状态。</summary>
public enum LmStudioModelFileResolutionStatus
{
    Success,
    InvalidModelSnapshot,
    UnsupportedEndpoint,
    CliUnavailable,
    CliTimedOut,
    CliFailed,
    InvalidJson,
    InvalidSettings,
    IdentityMismatch,
    UnsafePath,
    MissingFile,
    UnsupportedFileType,
    Ambiguous,
    Conflict,
    NoMatch
}

/// <summary>配置变更类型。</summary>
public enum ConfigMutationKind
{
    Add,
    Change,
    Remove,
    Restore
}

/// <summary>Secondary Override 在切换时的处理策略。</summary>
public enum SecondaryOverridePolicy
{
    Preserve,
    FollowMain,
    RestoreOriginal
}

/// <summary>自动压缩（AutoCompact）模式。</summary>
public enum AutoCompactMode
{
    Automatic,
    Manual
}

/// <summary>备份操作类型。</summary>
public enum BackupOperation
{
    InitialSnapshot,
    Switch,
    RestorePrevious,
    RestoreInitial,
    Manual
}

/// <summary>文件指纹：存在性、长度、最后写入时间与 SHA-256 哈希。</summary>
public sealed record FileFingerprint(
    bool Exists,
    long Length,
    DateTimeOffset? LastWriteTimeUtc,
    string Sha256)
{
    /// <summary>文件不存在时的指纹哨兵值。</summary>
    public static FileFingerprint Missing { get; } = new(false, 0, null, string.Empty);
}

/// <summary>文本文件格式：BOM、换行风格、末尾换行与混合换行标记。</summary>
public sealed record TextFileFormat(
    bool HasUtf8Bom,
    string NewLine,
    bool HasTrailingNewLine,
    bool HasMixedNewLines);

/// <summary>文本文件快照：原始字节、解码文本、格式与指纹的完整组合。</summary>
public sealed record TextFileSnapshot(
    string Path,
    byte[] Bytes,
    string Text,
    TextFileFormat Format,
    FileFingerprint Fingerprint);

/// <summary>一次配置键值变更（新增/修改/删除/恢复），IsSecret 标记脱敏展示。</summary>
public sealed record ConfigMutation(
    string KeyPath,
    ConfigMutationKind Kind,
    string? OldValue,
    string? NewValue,
    bool IsSecret = false);

/// <summary>补丁执行后的保留摘要：各节计数与未纳管文本是否原样保留。</summary>
public sealed record PreservationSummary(
    int McpServerCount,
    int ProjectCount,
    int HookSectionCount,
    int PluginSectionCount,
    bool UnmanagedTextPreserved);

/// <summary>上下文相关配置：模型/加载/配置三处上下文与自动压缩阈值。</summary>
public sealed record ContextConfiguration(
    int? ModelMaxContext,
    int? LoadedContext,
    int? CodexConfiguredContext,
    int? AutoCompactTokenLimit,
    bool IsSuggested);

/// <summary>LM Studio 加载参数全集（上下文长度、批大小、投机解码、KV 卸载等）。</summary>
public sealed record LmStudioLoadConfiguration(
    int? ContextLength = null,
    int? EvalBatchSize = null,
    int? PhysicalBatchSize = null,
    int? Parallel = null,
    bool? FlashAttention = null,
    int? ContextCheckpoints = null,
    string? ReasoningBudgetMessage = null,
    bool? SpeculativeDraftMtp = null,
    bool? SpeculativeDraftSimple = null,
    string? SpeculativeDraftModel = null,
    int? SpeculativeDraftMaxTokens = null,
    int? SpeculativeDraftMinTokens = null,
    double? SpeculativeDraftMinContinueProbability = null,
    bool? OffloadKvCacheToGpu = null,
    int? NumExperts = null);

/// <summary>LM Studio 提示词模板配置：类型、模板文本与停止串。</summary>
public sealed record LmStudioPromptTemplateConfiguration(
    string Type,
    string Template,
    IReadOnlyList<string> StopStrings);

/// <summary>已加载实例的运行时快照（端点、变体、量化、加载参数、TTL、指纹等）。</summary>
public sealed record LmStudioLoadedInstanceSnapshot(
    Uri Endpoint,
    string SourceModelKey,
    string InstanceId,
    string? SelectedVariant,
    string? Architecture,
    string? Quantization,
    string? Parameters,
    string? ModelType,
    int? MaxContextLength,
    LmStudioLoadConfiguration LoadConfiguration,
    int? RemainingTtlSeconds,
    bool RequiresAuthentication,
    DateTimeOffset CapturedAt,
    string Fingerprint,
    LmStudioLoadTarget? LoadTarget = null);

/// <summary>加载目标描述：模型键、可用变体与识别信息（用于 unload 后按原样重载）。</summary>
public sealed record LmStudioLoadTarget(
    string ModelKey,
    string? SelectedVariant,
    IReadOnlyList<string> AvailableVariants,
    string? Architecture,
    string? Quantization,
    string? Parameters,
    string? Format,
    int? MaxContextLength,
    string Fingerprint);

/// <summary>LM Studio API 失败的规范化描述。</summary>
public sealed record LmStudioApiFailure(
    int HttpStatus,
    string? ErrorType,
    string? ErrorCode,
    string? Parameter,
    string Message);

/// <summary>模型文件定位成功结果：磁盘路径与来源、变体等识别信息。</summary>
public sealed record LmStudioModelFileResolution(
    string FilePath,
    string SourceModelKey,
    string? SelectedVariant,
    string? Architecture,
    string? Quantization,
    string Source,
    string? ConcreteModelIdentifier = null);

/// <summary>模型文件定位尝试：状态 + 成功时的定位结果 + 失败/诊断说明。</summary>
public sealed record LmStudioModelFileResolutionAttempt(
    LmStudioModelFileResolutionStatus Status,
    LmStudioModelFileResolution? Resolution,
    string Diagnostic)
{
    /// <summary>定位成功且带有结果。</summary>
    public bool Succeeded => Status == LmStudioModelFileResolutionStatus.Success && Resolution is not null;
}

/// <summary>模型档案：跨 Provider 的统一模型描述（展示名、能力标记、加载状态、来源等）。</summary>
public sealed record ModelProfile(
    string Id,
    string DisplayName,
    ProviderKind Provider,
    string? Description = null,
    string? Quantization = null,
    string? Parameters = null,
    long? SizeBytes = null,
    bool? IsLoaded = null,
    int? MaxContextLength = null,
    int? LoadedContextLength = null,
    bool? TrainedForToolUse = null,
    bool? SupportsReasoning = null,
    bool? SupportsVision = null,
    IReadOnlyList<string>? ReasoningOptions = null,
    string? Source = null,
    string? LoadedInstanceId = null,
    string? MinimalClientVersion = null,
    bool IsStale = false,
    string? Architecture = null,
    string? DefaultReasoningEffort = null,
    string? ModelType = null,
    string? SourceModelKey = null,
    string? SelectedVariant = null,
    LmStudioLoadConfiguration? LoadedConfiguration = null,
    int? RemainingTtlSeconds = null,
    IReadOnlyList<string>? AvailableVariants = null,
    string? Format = null)
{
    /// <summary>下拉框展示标签；LM Studio 模型附加加载状态与上下文信息，其余 Provider 用展示名。</summary>
    [JsonIgnore]
    public string SelectionLabel
    {
        get
        {
            if (Provider != ProviderKind.LmStudio)
            {
                return DisplayName;
            }

            string loaded = IsLoaded == true ? "已加载" : "未加载";
            string context = LoadedContextLength is int actual ? $"Context {actual:N0}" + (MaxContextLength is int maximum ? $" / Max {maximum:N0}" : string.Empty) : "Context 未知";
            return $"{DisplayName} | {ModelType ?? "类型未知"} | {Quantization ?? "量化未知"} | {Parameters ?? "参数未知"} | {loaded} | {context}";
        }
    }
}

/// <summary>LM Studio 端点探测结果：端点与判定来源。</summary>
public sealed record LmStudioEndpointDetection(Uri Endpoint, string Source);

/// <summary>Provider 能力快照（命名工具、图像生成、联网搜索）。</summary>
public sealed record ProviderCapabilitySnapshot(
    bool? NamespaceTools,
    bool? ImageGeneration,
    bool? WebSearch,
    string Source);

/// <summary>单项能力检查结果。</summary>
public sealed record CompatibilityResult(
    string Capability,
    CompatibilityStatus Status,
    string Detail,
    DateTimeOffset CheckedAt,
    string? FailureCode = null);

/// <summary>指令层级探测单步结果：是否通过与 HTTP 状态码。</summary>
public sealed record CodexInstructionProbeStepResult(
    bool Passed,
    int? HttpStatus);

/// <summary>指令层级探测结果：控制调用 + 三步层级（首条 developer、会话控制、续轮 developer）。</summary>
public sealed record CodexInstructionHierarchyProbeResult(
    CodexInstructionProbeStepResult Control,
    CodexInstructionProbeStepResult LeadingDeveloper,
    CodexInstructionProbeStepResult ConversationControl,
    CodexInstructionProbeStepResult ContinuationDeveloper,
    string? FailureCode,
    string Detail,
    DateTimeOffset CheckedAt)
{
    /// <summary>控制调用是否通过。</summary>
    public bool ControlPassed => Control.Passed;

    /// <summary>三步层级是否全部通过。</summary>
    public bool HierarchyPassed => LeadingDeveloper.Passed && ConversationControl.Passed && ContinuationDeveloper.Passed;

    /// <summary>控制调用的 HTTP 状态码。</summary>
    public int? ControlHttpStatus => Control.HttpStatus;

    /// <summary>层级三步中第一个可用的 HTTP 状态码（续轮 → 会话 → 首条）。</summary>
    public int? HierarchyHttpStatus => ContinuationDeveloper.HttpStatus ?? ConversationControl.HttpStatus ?? LeadingDeveloper.HttpStatus;

    /// <summary>整体兼容结论：控制与层级全部通过。</summary>
    public bool IsCompatible => ControlPassed && HierarchyPassed;
}

/// <summary>一次完整兼容性测试报告：Provider、模型与各项能力结果。</summary>
public sealed record CompatibilityReport(
    ProviderKind Provider,
    string Model,
    IReadOnlyList<CompatibilityResult> Results);

/// <summary>Codex CLI 冒烟测试结果。</summary>
public sealed record SmokeTestResult(
    bool Passed,
    string Directory,
    int? ExitCode,
    IReadOnlyList<CompatibilityResult> Results,
    string Summary);

/// <summary>扫到的一条 Secondary Model Override（文件、键路径、模型与可编辑性）。</summary>
public sealed record SecondaryModelOverride(
    string FilePath,
    string KeyPath,
    string Model,
    string? Provider,
    bool IsPotentialCloudRequest,
    bool CanEdit,
    string Detail,
    string? RawTomlValue = null);

/// <summary>Secondary Override 的目标定位（文件 + 键路径）。</summary>
public sealed record SecondaryOverrideTarget(
    string FilePath,
    string KeyPath);

/// <summary>Codex 运行环境信息：主目录、版本、运行中进程与当前 Provider/模型等。</summary>
public sealed record CodexEnvironmentInfo(
    string CodexHome,
    string ConfigPath,
    string? DesktopVersion,
    string? CliVersion,
    bool IsRunning,
    IReadOnlyList<string> RunningProcesses,
    ProviderKind CurrentProvider,
    string? CurrentProviderId,
    string? CurrentModel,
    string? ReasoningEffort,
    bool ModelsJsonExists,
    bool DeepSeekOfficialBackupExists,
    FileFingerprint ConfigFingerprint,
    string? Warning);

/// <summary>Provider 探测结果。</summary>
public sealed record ProviderProbeResult(
    bool IsAvailable,
    string Summary,
    string? Version = null,
    Uri? Endpoint = null,
    int? HttpStatus = null,
    bool RequiresAuthentication = false);

/// <summary>切换请求：目标 Provider/模型与全部可选上下文（推理力度、上下文窗口、LM Studio 参数等）。</summary>
public sealed record SwitchRequest(
    ProviderKind TargetProvider,
    string TargetModel,
    string? ReasoningEffort = null,
    int? ContextWindow = null,
    int? AutoCompactTokenLimit = null,
    SecondaryOverridePolicy SecondaryOverridePolicy = SecondaryOverridePolicy.Preserve,
    string? LmStudioProviderId = null,
    Uri? LmStudioEndpoint = null,
    bool LmStudioRequiresAuthentication = false,
    string? CredentialHelperPath = null,
    string? DeepSeekCatalogPath = null,
    bool? TargetSupportsToolUse = null,
    bool? TargetSupportsReasoning = null,
    string? TargetModelType = null,
    string? SecondaryOverrideSelectionJson = null,
    string? TargetAllowedCodexReasoningEfforts = null,
    int? ToolOutputTokenLimit = null,
    AutoCompactMode? AutoCompactMode = null,
    GlmPlatform? GlmPlatform = null,
    string? GlmCatalogPath = null);

/// <summary>计划中的单文件变更：期望指纹、候选字节、变更明细与提交后校验器。</summary>
public sealed record PlannedFileChange(
    string Path,
    FileFingerprint ExpectedFingerprint,
    byte[]? CandidateBytes,
    IReadOnlyList<ConfigMutation> Mutations,
    Func<byte[], ValueTask>? Validator = null,
    bool CommitLast = false);

/// <summary>切换计划：请求、来源上下文、计划文件、变更、警告与计划哈希。</summary>
public sealed record SwitchPlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    SwitchRequest Request,
    ProviderKind SourceProvider,
    string? SourceModel,
    IReadOnlyList<PlannedFileChange> Files,
    IReadOnlyList<ConfigMutation> Mutations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SecondaryModelOverride> SecondaryOverrides,
    PreservationSummary Preservation,
    string PlanHash,
    CodexInstructionHierarchyProbeResult? LmStudioPreflight = null)
{
    // 影响候选结果但未必产生文件变更的输入指纹；null 保持既有调用方“仅 Files”的契约
    internal IReadOnlyDictionary<string, FileFingerprint>? ReadFingerprints { get; init; }
}

/// <summary>GGUF 聊天模板分析：文件信息、GGUF 版本与模板文本及其哈希。</summary>
public sealed record GgufChatTemplateAnalysis(
    string FilePath,
    string FileName,
    long FileLength,
    DateTimeOffset LastWriteTimeUtc,
    uint GgufVersion,
    string? ModelName,
    string? Architecture,
    string ChatTemplate,
    string TemplateSha256);

/// <summary>模板修复预览：状态、说明、补丁后模板与规则版本（不落盘）。</summary>
public sealed record PromptTemplateRepairPreview(
    PromptTemplateRepairStatus Status,
    string Detail,
    string? PatchedTemplate,
    string? PatchedTemplateSha256,
    string RuleVersion);

/// <summary>模板修复导出工件：输出目录与各文件路径及前后哈希。</summary>
public sealed record PromptTemplateRepairArtifact(
    string Directory,
    string OriginalTemplatePath,
    string PatchedTemplatePath,
    string ManifestPath,
    string ApplyInstructionsPath,
    string OriginalTemplateSha256,
    string PatchedTemplateSha256);

/// <summary>LM Studio 模板修复计划：事务 ID、失败码、原始实例/模型文件/模板分析与持久化计划。</summary>
public sealed record LmStudioTemplateRepairPlan(
    Guid TransactionId,
    DateTimeOffset CreatedAt,
    string FailureCode,
    LmStudioLoadedInstanceSnapshot OriginalInstance,
    LmStudioModelFileResolution ModelFile,
    GgufChatTemplateAnalysis GgufAnalysis,
    PromptTemplateRepairPreview TemplatePreview,
    LmStudioRuntimeTemplateProvenance OriginalRuntimeTemplate,
    CodexInstructionHierarchyProbeResult OriginalHierarchyProbe,
    string? OriginalRuntimeTemplateText = null,
    LmStudioPerModelDefaultsPlan? PersistentDefaults = null,
    string? LmStudioVersion = null);

/// <summary>每模型默认值计划：目标文件、原始/候选指纹与模板字段状态、规则版本与变更类型。</summary>
public sealed record LmStudioPerModelDefaultsPlan(
    string ConcreteModelIdentifier,
    string FilePath,
    string LmStudioVersion,
    FileFingerprint OriginalFingerprint,
    FileFingerprint CandidateFingerprint,
    LmStudioPersistentTemplateFieldState OriginalFieldState,
    string? OriginalRuleVersion,
    string? OriginalTemplateSha256,
    string TargetRuleVersion,
    string TargetTemplateSha256,
    LmStudioPerModelDefaultsMutation Mutation,
    byte[] OriginalBytes,
    byte[] CandidateBytes);

/// <summary>每模型默认值的加密备份工件：路径与明文/密文哈希。</summary>
public sealed record LmStudioDefaultsBackupArtifact(
    string Path,
    string PlaintextSha256,
    string EncryptedSha256);

/// <summary>每模型默认值恢复结果。</summary>
public sealed record LmStudioDefaultsRestoreResult(
    bool Succeeded,
    bool RecoveryBlocked,
    string Detail,
    FileFingerprint? RestoredFingerprint = null);

/// <summary>每模型默认值持久化巡检结果。</summary>
public sealed record LmStudioPersistenceInspection(
    LmStudioPersistenceStatus Status,
    string? FilePath,
    FileFingerprint? Fingerprint,
    string? TemplateSha256,
    string Detail);

/// <summary>运行时模板来源信息：模式、规则版本、模板哈希与佐证事务 ID。</summary>
public sealed record LmStudioRuntimeTemplateProvenance(
    LmStudioRuntimeTemplateMode Mode,
    string? RuleVersion = null,
    string? TemplateSha256 = null,
    Guid? EvidenceTransactionId = null);

/// <summary>模板修复执行结果：计划、修复后实例、层级探测与事务记录路径。</summary>
public sealed record LmStudioTemplateRepairResult(
    LmStudioTemplateRepairPlan Plan,
    LmStudioLoadedInstanceSnapshot PatchedInstance,
    CodexInstructionHierarchyProbeResult HierarchyProbe,
    string TransactionPath);

/// <summary>模板回滚结果。</summary>
public sealed record LmStudioRollbackResult(
    bool Succeeded,
    string Detail,
    LmStudioLoadedInstanceSnapshot? RestoredInstance,
    string TransactionPath);

/// <summary>恢复评估中的候选实例：快照、是否匹配原始、层级探测与是否复现原失败。</summary>
public sealed record LmStudioRecoveryCandidate(
    LmStudioLoadedInstanceSnapshot Snapshot,
    bool MatchesOriginalSnapshot,
    CodexInstructionHierarchyProbeResult? HierarchyProbe,
    bool ReproducesOriginalFailure);

/// <summary>恢复评估结论：处置方式、候选实例、待卸载实例与状态指纹等。</summary>
public sealed record LmStudioRecoveryAssessment(
    Guid TransactionId,
    LmStudioRecoveryDisposition Disposition,
    IReadOnlyList<LmStudioRecoveryCandidate> Candidates,
    string? InstanceToUnload,
    bool RequiresLifecycleMutation,
    bool IsLegacyJournal,
    string StateFingerprint,
    string Detail,
    FileFingerprint? CurrentDefaultsFingerprint = null,
    bool RequiresPersistenceRecovery = false);

/// <summary>模板事务记录：状态机的完整持久化形态（含恢复所需的全部上下文）。</summary>
public sealed record LmStudioTemplateTransactionRecord(
    int SchemaVersion,
    Guid TransactionId,
    LmStudioTemplateTransactionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LmStudioLoadedInstanceSnapshot OriginalInstance,
    string FailureCode,
    string GgufFilePath,
    string GgufFileName,
    long GgufLength,
    DateTimeOffset GgufLastWriteTimeUtc,
    uint GgufVersion,
    string OriginalTemplateSha256,
    string PatchedTemplateSha256,
    string RuleVersion,
    string? PatchedInstanceId,
    string? Detail,
    string? LoadModelKey = null,
    LmStudioTemplateTransactionState? LastStableState = null,
    LmStudioLifecycleStage FailureStage = LmStudioLifecycleStage.None,
    LmStudioApiFailure? LastApiFailure = null,
    IReadOnlyList<string>? SameSourceInstanceIdsBeforeLoad = null,
    IReadOnlyList<string>? SameSourceInstanceIdsAfterLoad = null,
    string? CandidateInstanceId = null,
    int RecoveryAttemptCount = 0,
    LmStudioLifecycleStage LastRecoveryFailureStage = LmStudioLifecycleStage.None,
    LmStudioRuntimeTemplateMode OriginalRuntimeTemplateMode = LmStudioRuntimeTemplateMode.BuiltIn,
    string? OriginalRuntimeRuleVersion = null,
    string? OriginalRuntimeTemplateSha256 = null,
    Guid? OriginalRuntimeEvidenceTransactionId = null,
    string? TargetRuntimeRuleVersion = null,
    CodexInstructionHierarchyProbeResult? OriginalHierarchyProbe = null,
    string? ConcreteModelIdentifier = null,
    string? PerModelDefaultsPath = null,
    FileFingerprint? OriginalDefaultsFingerprint = null,
    LmStudioPersistentTemplateFieldState? OriginalPersistentTemplateState = null,
    string? OriginalPersistentRuleVersion = null,
    string? OriginalPersistentTemplateSha256 = null,
    string? TargetPersistentRuleVersion = null,
    string? TargetPersistentTemplateSha256 = null,
    string? CandidateDefaultsSha256 = null,
    string? EncryptedDefaultsBackupPath = null,
    string? DefaultsBackupPlaintextSha256 = null,
    LmStudioPersistenceStage PersistenceStage = LmStudioPersistenceStage.None,
    string? LmStudioVersion = null);

/// <summary>单个 Provider 的配置状态快照（根键值 + 表体 + 来源哈希），用于切换时还原。</summary>
public sealed record ProviderState(
    ProviderKind Provider,
    DateTimeOffset CapturedAt,
    Dictionary<string, string?> RootValues,
    Dictionary<string, string?> TableBodies,
    string SourceConfigSha256);

/// <summary>appsettings.json 的根对象。</summary>
public sealed class AppSettings
{
    /// <summary>设置模式版本。</summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>Codex 主目录覆盖（null 表示用默认解析）。</summary>
    public string? CodexHomeOverride { get; set; }

    /// <summary>LM Studio 端点地址。</summary>
    public string LmStudioEndpoint { get; set; } = "http://127.0.0.1:1234";

    /// <summary>GLM 平台选择（BigModel / Zai）。</summary>
    public string GlmPlatform { get; set; } = nameof(Models.GlmPlatform.BigModel);

    /// <summary>按模型 ID 索引的偏好设置。</summary>
    public Dictionary<string, ModelPreference> ModelPreferences { get; set; } = new(StringComparer.Ordinal);

    /// <summary>按 Provider 索引的配置状态快照。</summary>
    public Dictionary<string, ProviderState> ProviderStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Secondary Override 的原始值记录（键用专用比较器）。</summary>
    public Dictionary<string, string> SecondaryOverrideOriginals { get; set; } = new(Codex.SecondaryOverrideKeyComparer.Instance);

    /// <summary>上次纳管配置的 SHA-256。</summary>
    public string? LastManagedConfigSha256 { get; set; }

    /// <summary>上次纳管配置的时间。</summary>
    public DateTimeOffset? LastManagedAt { get; set; }

    /// <summary>启动时是否创建初始快照。</summary>
    public bool CreateInitialSnapshotOnLaunch { get; set; } = true;
}

/// <summary>设置加载结果：设置本体与（发生损坏恢复时的）隔离文件信息。</summary>
public sealed record AppSettingsLoadResult(
    AppSettings Settings,
    string? RecoveredCorruptFilePath = null,
    string? RecoveredCorruptSha256 = null,
    string? Warning = null,
    string? RecoveredCorruptExceptionType = null)
{
    /// <summary>是否发生了损坏设置的恢复。</summary>
    public bool RecoveredCorruptSettings => !string.IsNullOrWhiteSpace(RecoveredCorruptFilePath);
}

/// <summary>单个模型的偏好设置（上下文与自动压缩相关）。</summary>
public sealed class ModelPreference
{
    /// <summary>上次加载该模型时的上下文长度。</summary>
    public int? LastLoadedContext { get; set; }

    /// <summary>Codex 配置中的上下文长度。</summary>
    public int? CodexContext { get; set; }

    /// <summary>自动压缩阈值（token）。</summary>
    public int? AutoCompactTokenLimit { get; set; }

    /// <summary>自动压缩模式。</summary>
    public AutoCompactMode? AutoCompactMode { get; set; }

    /// <summary>生成阈值的策略版本。</summary>
    public int? AutoCompactPolicyVersion { get; set; }

    /// <summary>工具输出 token 上限。</summary>
    public int? ToolOutputTokenLimit { get; set; }
}

/// <summary>备份清单：操作、版本、切换上下文与文件列表。</summary>
public sealed class BackupManifest
{
    /// <summary>清单模式版本。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>触发本次备份的操作。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupOperation Operation { get; set; }

    /// <summary>备份创建时间（字符串化）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>创建备份的应用版本。</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>创建备份时的 Codex 版本。</summary>
    public string? CodexVersion { get; set; }

    /// <summary>切换来源 Provider。</summary>
    public string? SourceProvider { get; set; }

    /// <summary>切换来源模型。</summary>
    public string? SourceModel { get; set; }

    /// <summary>切换目标 Provider。</summary>
    public string? TargetProvider { get; set; }

    /// <summary>切换目标模型。</summary>
    public string? TargetModel { get; set; }

    /// <summary>备份包含的文件清单。</summary>
    public List<BackupFileManifest> Files { get; set; } = [];

    /// <summary>本次备份涉及的配置键。</summary>
    public List<string> ChangedKeys { get; set; } = [];
}

/// <summary>备份中单个文件的清单项：相对名、原路径、哈希与格式标记。</summary>
public sealed class BackupFileManifest
{
    /// <summary>备份内的相对文件名。</summary>
    public string RelativeName { get; set; } = string.Empty;

    /// <summary>原始完整路径。</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>备份时原文件是否存在。</summary>
    public bool Existed { get; set; }

    /// <summary>文件长度。</summary>
    public long Length { get; set; }

    /// <summary>文件内容 SHA-256。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>是否带 UTF-8 BOM。</summary>
    public bool Utf8Bom { get; set; }

    /// <summary>换行风格。</summary>
    public string NewLine { get; set; } = string.Empty;
}
