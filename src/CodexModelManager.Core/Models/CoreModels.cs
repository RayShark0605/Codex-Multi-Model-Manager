using System.Text.Json.Serialization;

namespace CodexModelManager.Core.Models;

public enum ProviderKind
{
    Unknown,
    OpenAI,
    DeepSeek,
    LmStudio
}

public enum CompatibilityStatus
{
    Supported,
    LikelySupported,
    Untested,
    KnownLimitation,
    Failed
}

public enum PromptTemplateRepairStatus
{
    Supported,
    UpgradeRequired,
    AlreadyCompatible,
    Unsupported
}

public enum LmStudioRuntimeTemplateMode
{
    BuiltIn,
    ManagerRule
}

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

public enum LmStudioPersistentTemplateFieldState
{
    Missing,
    ManagerV2,
    ManagerV3
}

public enum LmStudioPerModelDefaultsMutation
{
    Add,
    Upgrade,
    NoOp
}

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

public enum LmStudioRecoveryDisposition
{
    AlreadyRestored,
    LoadOriginal,
    UnloadKnownPatchAndLoadOriginal,
    BlockedAmbiguous
}

public static class CompatibilityFailureCodes
{
    public const string LmStudioChatTemplateSystemOrder = "lmstudio-chat-template-system-order";
    public const string LmStudioChatTemplateDeveloperRole = "lmstudio-chat-template-developer-role";
    public const string LmStudioChatTemplateContinuationInstructionOrder = "lmstudio-chat-template-continuation-instruction-order";
    public const string ResponsesControlFailed = "responses-control-failed";
    public const string ResponsesConversationControlFailed = "responses-conversation-control-failed";
    public const string AuthenticationRequired = "authentication-required";
    public const string Timeout = "timeout";
    public const string OtherProviderError = "other-provider-error";
    public const string LmStudioLoadedInstanceMissing = "lmstudio-loaded-instance-missing";
    public const string LmStudioLoadedContextChanged = "lmstudio-loaded-context-changed";
    public const string LmStudioLoadedInstanceChanged = "lmstudio-loaded-instance-changed";
    public const string LmStudioLifecycleFailed = "lmstudio-lifecycle-failed";
}

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

public enum ConfigMutationKind
{
    Add,
    Change,
    Remove,
    Restore
}

public enum SecondaryOverridePolicy
{
    Preserve,
    FollowMain,
    RestoreOriginal
}

public enum AutoCompactMode
{
    Automatic,
    Manual
}

public enum BackupOperation
{
    InitialSnapshot,
    Switch,
    RestorePrevious,
    RestoreInitial,
    Manual
}

public sealed record FileFingerprint(
    bool Exists,
    long Length,
    DateTimeOffset? LastWriteTimeUtc,
    string Sha256)
{
    public static FileFingerprint Missing { get; } = new(false, 0, null, string.Empty);
}

public sealed record TextFileFormat(
    bool HasUtf8Bom,
    string NewLine,
    bool HasTrailingNewLine,
    bool HasMixedNewLines);

public sealed record TextFileSnapshot(
    string Path,
    byte[] Bytes,
    string Text,
    TextFileFormat Format,
    FileFingerprint Fingerprint);

public sealed record ConfigMutation(
    string KeyPath,
    ConfigMutationKind Kind,
    string? OldValue,
    string? NewValue,
    bool IsSecret = false);

public sealed record PreservationSummary(
    int McpServerCount,
    int ProjectCount,
    int HookSectionCount,
    int PluginSectionCount,
    bool UnmanagedTextPreserved);

public sealed record ContextConfiguration(
    int? ModelMaxContext,
    int? LoadedContext,
    int? CodexConfiguredContext,
    int? AutoCompactTokenLimit,
    bool IsSuggested);

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

public sealed record LmStudioPromptTemplateConfiguration(
    string Type,
    string Template,
    IReadOnlyList<string> StopStrings);

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

public sealed record LmStudioApiFailure(
    int HttpStatus,
    string? ErrorType,
    string? ErrorCode,
    string? Parameter,
    string Message);

public sealed record LmStudioModelFileResolution(
    string FilePath,
    string SourceModelKey,
    string? SelectedVariant,
    string? Architecture,
    string? Quantization,
    string Source,
    string? ConcreteModelIdentifier = null);

public sealed record LmStudioModelFileResolutionAttempt(
    LmStudioModelFileResolutionStatus Status,
    LmStudioModelFileResolution? Resolution,
    string Diagnostic)
{
    public bool Succeeded => Status == LmStudioModelFileResolutionStatus.Success && Resolution is not null;
}

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
    [JsonIgnore]
    public string SelectionLabel
    {
        get
        {
            if (Provider != ProviderKind.LmStudio) return DisplayName;
            string loaded = IsLoaded == true ? "已加载" : "未加载";
            string context = LoadedContextLength is int actual
                ? $"Context {actual:N0}" + (MaxContextLength is int maximum ? $" / Max {maximum:N0}" : string.Empty)
                : "Context 未知";
            return $"{DisplayName} | {ModelType ?? "类型未知"} | {Quantization ?? "量化未知"} | {Parameters ?? "参数未知"} | {loaded} | {context}";
        }
    }
}

public sealed record LmStudioEndpointDetection(Uri Endpoint, string Source);

public sealed record ProviderCapabilitySnapshot(
    bool? NamespaceTools,
    bool? ImageGeneration,
    bool? WebSearch,
    string Source);

public sealed record CompatibilityResult(
    string Capability,
    CompatibilityStatus Status,
    string Detail,
    DateTimeOffset CheckedAt,
    string? FailureCode = null);

public sealed record CodexInstructionProbeStepResult(
    bool Passed,
    int? HttpStatus);

public sealed record CodexInstructionHierarchyProbeResult(
    CodexInstructionProbeStepResult Control,
    CodexInstructionProbeStepResult LeadingDeveloper,
    CodexInstructionProbeStepResult ConversationControl,
    CodexInstructionProbeStepResult ContinuationDeveloper,
    string? FailureCode,
    string Detail,
    DateTimeOffset CheckedAt)
{
    public bool ControlPassed => Control.Passed;

    public bool HierarchyPassed => LeadingDeveloper.Passed && ConversationControl.Passed && ContinuationDeveloper.Passed;

    public int? ControlHttpStatus => Control.HttpStatus;

    public int? HierarchyHttpStatus => ContinuationDeveloper.HttpStatus ?? ConversationControl.HttpStatus ?? LeadingDeveloper.HttpStatus;

    public bool IsCompatible => ControlPassed && HierarchyPassed;
}

public sealed record CompatibilityReport(
    ProviderKind Provider,
    string Model,
    IReadOnlyList<CompatibilityResult> Results);

public sealed record SmokeTestResult(
    bool Passed,
    string Directory,
    int? ExitCode,
    IReadOnlyList<CompatibilityResult> Results,
    string Summary);

public sealed record SecondaryModelOverride(
    string FilePath,
    string KeyPath,
    string Model,
    string? Provider,
    bool IsPotentialCloudRequest,
    bool CanEdit,
    string Detail,
    string? RawTomlValue = null);

public sealed record SecondaryOverrideTarget(
    string FilePath,
    string KeyPath);

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

public sealed record ProviderProbeResult(
    bool IsAvailable,
    string Summary,
    string? Version = null,
    Uri? Endpoint = null,
    int? HttpStatus = null,
    bool RequiresAuthentication = false);

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
    AutoCompactMode? AutoCompactMode = null);

public sealed record PlannedFileChange(
    string Path,
    FileFingerprint ExpectedFingerprint,
    byte[]? CandidateBytes,
    IReadOnlyList<ConfigMutation> Mutations,
    Func<byte[], ValueTask>? Validator = null,
    bool CommitLast = false);

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
    CodexInstructionHierarchyProbeResult? LmStudioPreflight = null);

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

public sealed record PromptTemplateRepairPreview(
    PromptTemplateRepairStatus Status,
    string Detail,
    string? PatchedTemplate,
    string? PatchedTemplateSha256,
    string RuleVersion);

public sealed record PromptTemplateRepairArtifact(
    string Directory,
    string OriginalTemplatePath,
    string PatchedTemplatePath,
    string ManifestPath,
    string ApplyInstructionsPath,
    string OriginalTemplateSha256,
    string PatchedTemplateSha256);

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

public sealed record LmStudioDefaultsBackupArtifact(
    string Path,
    string PlaintextSha256,
    string EncryptedSha256);

public sealed record LmStudioDefaultsRestoreResult(
    bool Succeeded,
    bool RecoveryBlocked,
    string Detail,
    FileFingerprint? RestoredFingerprint = null);

public sealed record LmStudioPersistenceInspection(
    LmStudioPersistenceStatus Status,
    string? FilePath,
    FileFingerprint? Fingerprint,
    string? TemplateSha256,
    string Detail);

public sealed record LmStudioRuntimeTemplateProvenance(
    LmStudioRuntimeTemplateMode Mode,
    string? RuleVersion = null,
    string? TemplateSha256 = null,
    Guid? EvidenceTransactionId = null);

public sealed record LmStudioTemplateRepairResult(
    LmStudioTemplateRepairPlan Plan,
    LmStudioLoadedInstanceSnapshot PatchedInstance,
    CodexInstructionHierarchyProbeResult HierarchyProbe,
    string TransactionPath);

public sealed record LmStudioRollbackResult(
    bool Succeeded,
    string Detail,
    LmStudioLoadedInstanceSnapshot? RestoredInstance,
    string TransactionPath);

public sealed record LmStudioRecoveryCandidate(
    LmStudioLoadedInstanceSnapshot Snapshot,
    bool MatchesOriginalSnapshot,
    CodexInstructionHierarchyProbeResult? HierarchyProbe,
    bool ReproducesOriginalFailure);

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

public sealed record ProviderState(
    ProviderKind Provider,
    DateTimeOffset CapturedAt,
    Dictionary<string, string?> RootValues,
    Dictionary<string, string?> TableBodies,
    string SourceConfigSha256);

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;

    public string? CodexHomeOverride { get; set; }

    public string LmStudioEndpoint { get; set; } = "http://127.0.0.1:1234";

    public Dictionary<string, ModelPreference> ModelPreferences { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, ProviderState> ProviderStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> SecondaryOverrideOriginals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? LastManagedConfigSha256 { get; set; }

    public DateTimeOffset? LastManagedAt { get; set; }

    public bool CreateInitialSnapshotOnLaunch { get; set; } = true;
}

public sealed record AppSettingsLoadResult(
    AppSettings Settings,
    string? RecoveredCorruptFilePath = null,
    string? RecoveredCorruptSha256 = null,
    string? Warning = null,
    string? RecoveredCorruptExceptionType = null)
{
    public bool RecoveredCorruptSettings => !string.IsNullOrWhiteSpace(RecoveredCorruptFilePath);
}

public sealed class ModelPreference
{
    public int? LastLoadedContext { get; set; }

    public int? CodexContext { get; set; }

    public int? AutoCompactTokenLimit { get; set; }

    public AutoCompactMode? AutoCompactMode { get; set; }

    public int? AutoCompactPolicyVersion { get; set; }

    public int? ToolOutputTokenLimit { get; set; }
}

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackupOperation Operation { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public string? CodexVersion { get; set; }

    public string? SourceProvider { get; set; }

    public string? SourceModel { get; set; }

    public string? TargetProvider { get; set; }

    public string? TargetModel { get; set; }

    public List<BackupFileManifest> Files { get; set; } = [];

    public List<string> ChangedKeys { get; set; } = [];
}

public sealed class BackupFileManifest
{
    public string RelativeName { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public bool Existed { get; set; }

    public long Length { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public bool Utf8Bom { get; set; }

    public string NewLine { get; set; } = string.Empty;
}
