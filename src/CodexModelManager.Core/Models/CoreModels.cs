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
    AlreadyCompatible,
    Unsupported
}

public static class CompatibilityFailureCodes
{
    public const string LmStudioChatTemplateSystemOrder = "lmstudio-chat-template-system-order";
    public const string LmStudioChatTemplateDeveloperRole = "lmstudio-chat-template-developer-role";
    public const string ResponsesControlFailed = "responses-control-failed";
    public const string AuthenticationRequired = "authentication-required";
    public const string Timeout = "timeout";
    public const string OtherProviderError = "other-provider-error";
    public const string LmStudioLoadedInstanceMissing = "lmstudio-loaded-instance-missing";
    public const string LmStudioLoadedContextChanged = "lmstudio-loaded-context-changed";
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
    string? SourceModelKey = null)
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

public sealed record CodexInstructionHierarchyProbeResult(
    bool ControlPassed,
    bool HierarchyPassed,
    int? ControlHttpStatus,
    int? HierarchyHttpStatus,
    string? FailureCode,
    string Detail,
    DateTimeOffset CheckedAt)
{
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
    string? TargetAllowedCodexReasoningEfforts = null);

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

public sealed record ProviderState(
    ProviderKind Provider,
    DateTimeOffset CapturedAt,
    Dictionary<string, string?> RootValues,
    Dictionary<string, string?> TableBodies,
    string SourceConfigSha256);

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string? CodexHomeOverride { get; set; }

    public string LmStudioEndpoint { get; set; } = "http://127.0.0.1:1234";

    public Dictionary<string, ModelPreference> ModelPreferences { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, ProviderState> ProviderStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> SecondaryOverrideOriginals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? LastManagedConfigSha256 { get; set; }

    public DateTimeOffset? LastManagedAt { get; set; }

    public bool CreateInitialSnapshotOnLaunch { get; set; } = true;
}

public sealed class ModelPreference
{
    public int? LastLoadedContext { get; set; }

    public int? CodexContext { get; set; }

    public int? AutoCompactTokenLimit { get; set; }
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
