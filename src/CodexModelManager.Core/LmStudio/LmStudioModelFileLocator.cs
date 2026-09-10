using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// LM Studio 模型文件定位器：以 native loaded instance 快照为权威身份，
/// 通过 lms ls --variants 与 lms ps 两条独立证据链交叉定位唯一的 GGUF 文件。
/// 全程保守：身份字段不全、端点非 loopback、路径越出 models 根目录、
/// 双证据冲突或多候选一律失败，绝不猜测。
/// </summary>
public sealed class LmStudioModelFileLocator : ILmStudioModelFileLocator
{
    private static readonly Uri DefaultEndpoint = new("http://127.0.0.1:1234");
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    private readonly ILmsCliCommandRunner commandRunner;
    private readonly Func<string> userProfileProvider;

    /// <summary>默认构造：真实进程方式运行 lms CLI。</summary>
    public LmStudioModelFileLocator()
        : this(new ProcessLmsCliCommandRunner(), ResolveUserProfile)
    {
    }

    /// <summary>测试用构造：注入 CLI 运行器与用户目录提供者。</summary>
    internal LmStudioModelFileLocator(ILmsCliCommandRunner commandRunner, Func<string> userProfileProvider)
    {
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.userProfileProvider = userProfileProvider ?? throw new ArgumentNullException(nameof(userProfileProvider));
    }

    /// <summary>解析模型对应的 GGUF 文件（详见类说明），成功/失败都以尝试结果返回。</summary>
    public async Task<LmStudioModelFileResolutionAttempt> ResolveAsync(ModelProfile model, Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(endpoint);
        string[] missingIdentityFields = GetMissingAuthoritativeLoadedIdentityFields(model);
        if (missingIdentityFields.Length > 0)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidModelSnapshot, $"native loaded instance 快照缺少或不满足以下权威字段：{string.Join("、", missingIdentityFields)}；拒绝猜测 GGUF。");
        }

        try
        {
            LmStudioEndpointPolicy.Validate(endpoint);
        }
        catch (InvalidOperationException)
        {
            return Failure(LmStudioModelFileResolutionStatus.UnsupportedEndpoint, "LM Studio endpoint 不满足安全 URI 约束；自动 GGUF 定位已阻断。");
        }

        if (!endpoint.IsLoopback)
        {
            return Failure(LmStudioModelFileResolutionStatus.UnsupportedEndpoint, "lms ps 返回的是服务端文件路径；仅本机 loopback endpoint 允许自动定位 GGUF。");
        }

        string userProfile;
        IReadOnlyList<string> modelRoots;
        try
        {
            userProfile = Path.GetFullPath(userProfileProvider());
            string settingsPath = Path.Combine(userProfile, ".lmstudio", "settings.json");
            string? settingsJson = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false) : null;
            modelRoots = ReadModelRoots(settingsJson, userProfile);
        }
        catch (JsonException)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidSettings, "LM Studio settings.json 不是有效 JSON；拒绝推断 models 根目录。");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidSettings, "无法安全读取或规范化 LM Studio models 根目录；自动定位已阻断。");
        }

        LmsCliCommandResult variantsCommand = await commandRunner.RunAsync(["ls", "--json", "--variants"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (variantsCommand.Status == LmsCliCommandStatus.Unavailable)
        {
            return Failure(LmStudioModelFileResolutionStatus.CliUnavailable, "未找到或无法启动 lms CLI；请检查 LM Studio CLI 安装，或手工选择 GGUF。");
        }

        LmsCliCommandResult processesCommand = await commandRunner.RunAsync(
            ["ps", "--json", "--host", endpoint.DnsSafeHost, "--port", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        LmStudioModelFileResolutionAttempt? commandFailure = ResolveCommandFailure(variantsCommand, processesCommand);
        if (commandFailure is not null)
        {
            return commandFailure;
        }

        EvidenceResult variants = ParseCommand(variantsCommand, json => ResolveLsEvidence(model, json, modelRoots));
        EvidenceResult processes = ParseCommand(processesCommand, json => ResolvePsEvidence(model, json, modelRoots));

        LmStudioModelFileResolutionAttempt? blocking = ResolveBlockingFailure(variants, processes);
        if (blocking is not null)
        {
            return blocking;
        }

        // 双证据都有效时要求路径一致；只有一侧有效时采纳该侧
        if (variants.Resolution is not null && processes.Resolution is not null)
        {
            if (!variants.Resolution.FilePath.Equals(processes.Resolution.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(LmStudioModelFileResolutionStatus.Conflict, "lms ls 与 lms ps 分别解析到不同的有效 GGUF 路径；拒绝自动选择，请手工核对。");
            }

            return Success(processes.Resolution);
        }

        if (processes.Resolution is not null)
        {
            return Success(processes.Resolution);
        }

        if (variants.Resolution is not null)
        {
            return Success(variants.Resolution);
        }

        return ResolveNoMatchFailure(variantsCommand, processesCommand, variants, processes);
    }

    /// <summary>便捷入口：解析成功返回文件路径，否则 null。</summary>
    public static async Task<string?> TryResolveAsync(ModelProfile model, CancellationToken cancellationToken = default) =>
        (await TryResolveDetailedAsync(model, cancellationToken).ConfigureAwait(false))?.FilePath;

    /// <summary>便捷入口：以默认端点解析，返回完整定位结果或 null。</summary>
    public static async Task<LmStudioModelFileResolution?> TryResolveDetailedAsync(ModelProfile model, CancellationToken cancellationToken = default)
    {
        LmStudioModelFileResolutionAttempt attempt = await new LmStudioModelFileLocator().ResolveAsync(model, DefaultEndpoint, cancellationToken).ConfigureAwait(false);
        return attempt.Resolution;
    }

    /// <summary>离线入口：用已有的 lms ls 变体 JSON 与 settings JSON 直接解析（不启动 CLI）。</summary>
    public static LmStudioModelFileResolution? ResolveFromJson(ModelProfile model, string variantsJson, string? settingsJson, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        EvidenceResult result = ResolveLsEvidence(model, variantsJson, ReadModelRoots(settingsJson, userProfile));
        return result.Resolution;
    }

    /// <summary>离线入口：用已有的 lms ps JSON 解析为尝试结果（成功/失败带诊断）。</summary>
    internal static LmStudioModelFileResolutionAttempt ResolvePsFromJson(ModelProfile model, string processesJson, string? settingsJson, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(processesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        try
        {
            EvidenceResult result = ResolvePsEvidence(model, processesJson, ReadModelRoots(settingsJson, userProfile));
            return result.Resolution is not null ? Success(result.Resolution) : Failure(MapEvidenceStatus(result.Status), DiagnosticForEvidence(result));
        }
        catch (JsonException)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidJson, "lms ps --json 输出不是有效 JSON；自动定位已阻断。");
        }
        catch (InvalidDataException)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidSettings, "LM Studio settings.json 的 downloadsFolder 必须是绝对路径；自动定位已阻断。");
        }
    }

    /// <summary>列出模型快照缺失的权威身份字段（provider、加载状态、ID、source、type、架构、实际 context）。</summary>
    private static string[] GetMissingAuthoritativeLoadedIdentityFields(ModelProfile model)
    {
        List<string> missing = [];
        if (model.Provider != ProviderKind.LmStudio)
        {
            missing.Add("provider=lmstudio");
        }

        if (model.IsLoaded != true)
        {
            missing.Add("loaded=true");
        }

        if (string.IsNullOrWhiteSpace(model.LoadedInstanceId ?? model.Id))
        {
            missing.Add("loaded ID");
        }

        if (string.IsNullOrWhiteSpace(model.SourceModelKey))
        {
            missing.Add("source");
        }

        if (string.IsNullOrWhiteSpace(model.ModelType))
        {
            missing.Add("type");
        }

        if (string.IsNullOrWhiteSpace(model.Architecture))
        {
            missing.Add("architecture");
        }

        if (model.LoadedContextLength is not > 0)
        {
            missing.Add("实际 context");
        }

        return [.. missing];
    }

    /// <summary>执行一条 CLI 命令的解析：命令未成功 → CommandFailed；解析抛 JsonException → InvalidJson。</summary>
    private static EvidenceResult ParseCommand(LmsCliCommandResult command, Func<string, EvidenceResult> parser)
    {
        if (command.Status != LmsCliCommandStatus.Success || string.IsNullOrWhiteSpace(command.StandardOutput))
        {
            return new EvidenceResult(EvidenceStatus.CommandFailed, null);
        }

        try
        {
            return parser(command.StandardOutput);
        }
        catch (JsonException)
        {
            return new EvidenceResult(EvidenceStatus.InvalidJson, null);
        }
    }

    /// <summary>两条命令任一未成功时，按优先级归并为统一的 CLI 失败结果；全部成功返回 null。</summary>
    private static LmStudioModelFileResolutionAttempt? ResolveCommandFailure(LmsCliCommandResult variantsCommand, LmsCliCommandResult processesCommand)
    {
        if (variantsCommand.Status == LmsCliCommandStatus.Success &&
            processesCommand.Status == LmsCliCommandStatus.Success)
        {
            return null;
        }

        LmsCliCommandStatus status = HighestPriorityCommandStatus(variantsCommand.Status, processesCommand.Status);
        return status switch
        {
            LmsCliCommandStatus.Unavailable => Failure(LmStudioModelFileResolutionStatus.CliUnavailable, "未找到或无法启动 lms CLI；请检查 LM Studio CLI 安装，或手工选择 GGUF。"),
            LmsCliCommandStatus.TimedOut => Failure(LmStudioModelFileResolutionStatus.CliTimedOut, "lms CLI 查询超时；未使用不完整输出，请重试或手工选择 GGUF。"),
            _ => Failure(LmStudioModelFileResolutionStatus.CliFailed, "lms CLI 查询失败或输出超出安全上限；未记录任何原始进程输出，请手工选择 GGUF。"),
        };
    }

    /// <summary>阻断性失败归并：任一证据链出现非法 JSON、歧义候选或被拒路径即整体失败。</summary>
    private static LmStudioModelFileResolutionAttempt? ResolveBlockingFailure(EvidenceResult variants, EvidenceResult processes)
    {
        if (variants.Status == EvidenceStatus.InvalidJson || processes.Status == EvidenceStatus.InvalidJson)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidJson, "lms CLI 返回了非法 JSON；为避免忽略冲突证据，自动定位已阻断。");
        }

        if (variants.Status == EvidenceStatus.Ambiguous || processes.Status == EvidenceStatus.Ambiguous)
        {
            return Failure(LmStudioModelFileResolutionStatus.Ambiguous, "lms CLI 对同一 native loaded instance 给出多个有效 GGUF 候选；拒绝自动选择。");
        }

        EvidenceStatus[] rejectedEvidence =
        [
            EvidenceStatus.IdentityMismatch,
            EvidenceStatus.UnsafePath,
            EvidenceStatus.MissingFile,
            EvidenceStatus.UnsupportedFileType,
        ];
        foreach (EvidenceStatus status in rejectedEvidence)
        {
            EvidenceResult? rejected = variants.Status == status && !string.IsNullOrWhiteSpace(variants.Diagnostic)
                ? variants
                : processes.Status == status ? processes : variants.Status == status ? variants : null;
            if (rejected is not null)
            {
                return Failure(MapEvidenceStatus(status), DiagnosticForEvidence(rejected));
            }
        }

        return null;
    }

    /// <summary>双证据均无有效结果时的兜底归并：优先报告证据层失败，其次命令层失败，最后 NoMatch。</summary>
    private static LmStudioModelFileResolutionAttempt ResolveNoMatchFailure(LmsCliCommandResult variantsCommand, LmsCliCommandResult processesCommand, EvidenceResult variants, EvidenceResult processes)
    {
        EvidenceStatus evidenceStatus = HighestPriorityEvidenceStatus(variants.Status, processes.Status);
        if (evidenceStatus is not (EvidenceStatus.NoMatch or EvidenceStatus.CommandFailed))
        {
            EvidenceResult evidence = variants.Status == evidenceStatus ? variants : processes;
            return Failure(MapEvidenceStatus(evidenceStatus), DiagnosticForEvidence(evidence));
        }

        LmsCliCommandStatus commandStatus = HighestPriorityCommandStatus(variantsCommand.Status, processesCommand.Status);
        return commandStatus switch
        {
            LmsCliCommandStatus.Unavailable => Failure(LmStudioModelFileResolutionStatus.CliUnavailable, "未找到或无法启动 lms CLI；请检查 LM Studio CLI 安装，或手工选择 GGUF。"),
            LmsCliCommandStatus.TimedOut => Failure(LmStudioModelFileResolutionStatus.CliTimedOut, "lms CLI 查询超时；未使用不完整输出，请重试或手工选择 GGUF。"),
            LmsCliCommandStatus.Failed or LmsCliCommandStatus.OutputTooLarge => Failure(LmStudioModelFileResolutionStatus.CliFailed, "lms CLI 查询失败或输出超出安全上限；未记录任何原始进程输出，请手工选择 GGUF。"),
            _ => Failure(LmStudioModelFileResolutionStatus.NoMatch, "lms ls 与 lms ps 均未唯一定位当前 native loaded instance 的 GGUF；请手工选择并核对。"),
        };
    }

    /// <summary>证据状态的报告优先级：不安全路径 &gt; 类型不支持 &gt; 文件缺失 &gt; 身份不符 &gt; 无匹配 &gt; 命令失败。</summary>
    private static EvidenceStatus HighestPriorityEvidenceStatus(EvidenceStatus left, EvidenceStatus right)
    {
        EvidenceStatus[] priority =
        [
            EvidenceStatus.UnsafePath,
            EvidenceStatus.UnsupportedFileType,
            EvidenceStatus.MissingFile,
            EvidenceStatus.IdentityMismatch,
            EvidenceStatus.NoMatch,
            EvidenceStatus.CommandFailed,
        ];
        return priority.First(status => left == status || right == status);
    }

    /// <summary>命令状态的报告优先级：不可用 &gt; 超时 &gt; 输出超限 &gt; 失败 &gt; 成功。</summary>
    private static LmsCliCommandStatus HighestPriorityCommandStatus(LmsCliCommandStatus left, LmsCliCommandStatus right)
    {
        LmsCliCommandStatus[] priority =
        [
            LmsCliCommandStatus.Unavailable,
            LmsCliCommandStatus.TimedOut,
            LmsCliCommandStatus.OutputTooLarge,
            LmsCliCommandStatus.Failed,
            LmsCliCommandStatus.Success,
        ];
        return priority.First(status => left == status || right == status);
    }

    /// <summary>证据状态 → 对外解析状态的映射。</summary>
    private static LmStudioModelFileResolutionStatus MapEvidenceStatus(EvidenceStatus status) => status switch
    {
        EvidenceStatus.IdentityMismatch => LmStudioModelFileResolutionStatus.IdentityMismatch,
        EvidenceStatus.UnsafePath => LmStudioModelFileResolutionStatus.UnsafePath,
        EvidenceStatus.MissingFile => LmStudioModelFileResolutionStatus.MissingFile,
        EvidenceStatus.UnsupportedFileType => LmStudioModelFileResolutionStatus.UnsupportedFileType,
        EvidenceStatus.Ambiguous => LmStudioModelFileResolutionStatus.Ambiguous,
        EvidenceStatus.InvalidJson => LmStudioModelFileResolutionStatus.InvalidJson,
        _ => LmStudioModelFileResolutionStatus.NoMatch,
    };

    /// <summary>各证据状态对应的默认诊断文案。</summary>
    private static string DiagnosticForEvidence(EvidenceStatus status) => status switch
    {
        EvidenceStatus.IdentityMismatch => "lms ps 的 instance/source/type/architecture/quantization/context 与 native 快照不完全一致；拒绝自动定位。",
        EvidenceStatus.UnsafePath => "lms CLI 候选路径越出 LM Studio models 根目录或 publisher 路径不一致；拒绝自动定位。",
        EvidenceStatus.MissingFile => "lms CLI 候选指向的 GGUF 文件不存在；请刷新 LM Studio 索引或手工选择。",
        EvidenceStatus.UnsupportedFileType => "lms CLI 候选不是 .gguf 文件；拒绝自动定位。",
        EvidenceStatus.Ambiguous => "lms CLI 返回多个有效 GGUF 候选；拒绝自动选择。",
        EvidenceStatus.InvalidJson => "lms CLI 返回非法 JSON；自动定位已阻断。",
        _ => "lms CLI 未唯一定位当前 native loaded instance 的 GGUF；请手工选择并核对。",
    };

    /// <summary>取证据自带的诊断，缺省回落到状态默认文案。</summary>
    private static string DiagnosticForEvidence(EvidenceResult evidence) =>
        string.IsNullOrWhiteSpace(evidence.Diagnostic) ? DiagnosticForEvidence(evidence.Status) : evidence.Diagnostic;

    /// <summary>解析 lms ls --json --variants 证据：按 source key（及可选选中变体）筛出候选并解析路径。</summary>
    private static EvidenceResult ResolveLsEvidence(ModelProfile model, string variantsJson, IReadOnlyList<string> modelRoots)
    {
        string? sourceKey = model.SourceModelKey;
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return new EvidenceResult(EvidenceStatus.IdentityMismatch, null);
        }

        using JsonDocument document = JsonDocument.Parse(variantsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new EvidenceResult(EvidenceStatus.InvalidJson, null);
        }

        List<CliCandidate> candidates = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("model", out JsonElement wrapperModel) && wrapperModel.ValueKind == JsonValueKind.Object)
            {
                if (!StringEquals(GetString(wrapperModel, "modelKey"), sourceKey))
                {
                    continue;
                }

                string? selectedVariant = model.SelectedVariant ?? GetString(wrapperModel, "selectedVariant");
                if (item.TryGetProperty("variants", out JsonElement variants) && variants.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement variant in variants.EnumerateArray())
                    {
                        AddLsCandidate(candidates, variant, sourceKey, selectedVariant, model, "lms ls --json --variants");
                    }
                }

                if (string.IsNullOrWhiteSpace(selectedVariant))
                {
                    AddLsCandidate(candidates, wrapperModel, sourceKey, null, model, "lms ls --json --variants:model");
                }

                continue;
            }

            AddLsCandidate(candidates, item, sourceKey, model.SelectedVariant, model, "lms ls --json --variants:legacy");
        }

        return ResolveCandidates(candidates, modelRoots);
    }

    /// <summary>
    /// 解析 lms ps --json 证据：逐条核对 modelKey/identifier/publisher/source/type/architecture/
    /// quantization/context/format 九项权威字段，全部一致才生成候选，不一致字段计入诊断。
    /// </summary>
    private static EvidenceResult ResolvePsEvidence(ModelProfile model, string processesJson, IReadOnlyList<string> modelRoots)
    {
        using JsonDocument document = JsonDocument.Parse(processesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new EvidenceResult(EvidenceStatus.InvalidJson, null);
        }

        string loadedId = model.LoadedInstanceId ?? model.Id;
        string sourceKey = model.SourceModelKey!;
        bool sawObject = false;
        bool sawIdentityMismatch = false;
        var mismatchFields = new SortedSet<string>(StringComparer.Ordinal);
        List<CliCandidate> candidates = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            sawObject = true;
            string? modelKey = GetString(item, "modelKey");
            string? identifier = GetString(item, "identifier");
            string? publisher = GetString(item, "publisher");
            string? type = GetString(item, "type");
            string? architecture = GetString(item, "architecture");
            string? quantization = GetQuantization(item);
            int? contextLength = GetInt32(item, "contextLength");
            string? format = GetString(item, "format");
            string? path = GetString(item, "path");
            string? indexedModelIdentifier = GetString(item, "indexedModelIdentifier");
            bool modelKeyMatches = ModelKeyMatches(modelKey, loadedId, sourceKey);
            bool identifierMatches = StringEquals(identifier, loadedId);
            bool publisherPresent = !string.IsNullOrWhiteSpace(publisher);
            bool sourceMatches = SourceMatches(sourceKey, publisher, modelKey, path, indexedModelIdentifier);
            bool typeMatches = Exact(model.ModelType, type);
            bool architectureMatches = Exact(model.Architecture, architecture);
            bool quantizationMatches = OptionalExact(model.Quantization, quantization);
            bool contextMatches = model.LoadedContextLength == contextLength;
            bool formatMatches = string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase);
            if (!modelKeyMatches || !identifierMatches || !publisherPresent || !sourceMatches || !typeMatches ||
                !architectureMatches || !quantizationMatches || !contextMatches || !formatMatches)
            {
                // 记录不一致的字段名，便于诊断输出
                sawIdentityMismatch = true;
                if (!modelKeyMatches)
                {
                    mismatchFields.Add("modelKey（必须等于 loaded ID 或 source/load key）");
                }

                if (!identifierMatches)
                {
                    mismatchFields.Add("loaded identifier");
                }

                if (!publisherPresent)
                {
                    mismatchFields.Add("publisher");
                }

                if (!sourceMatches)
                {
                    mismatchFields.Add(IsGgufSourcePath(sourceKey) ? "source/path/indexedModelIdentifier" : "source/publisher/modelKey");
                }

                if (!typeMatches)
                {
                    mismatchFields.Add("type");
                }

                if (!architectureMatches)
                {
                    mismatchFields.Add("architecture");
                }

                if (!quantizationMatches)
                {
                    mismatchFields.Add("quantization");
                }

                if (!contextMatches)
                {
                    mismatchFields.Add("实际 context");
                }

                if (!formatMatches)
                {
                    mismatchFields.Add("format=gguf");
                }

                continue;
            }

            candidates.Add(new CliCandidate(sourceKey, model.SelectedVariant, architecture, quantization, [ExtractIndexedRelativePath(indexedModelIdentifier), path], "lms ps --json", publisher));
        }

        if (candidates.Count == 0)
        {
            return new EvidenceResult(
                sawObject && sawIdentityMismatch ? EvidenceStatus.IdentityMismatch : EvidenceStatus.NoMatch,
                null,
                mismatchFields.Count == 0
                    ? null
                    : $"lms ps 的以下权威字段与 native loaded instance 快照不一致：{string.Join("、", mismatchFields)}；拒绝自动定位。");
        }

        return ResolveCandidates(candidates, modelRoots);
    }

    /// <summary>
    /// source 一致性判定：source key 为 .gguf 路径时要求 publisher 一致且全部证据路径与 source 归一化相等；
    /// 否则按“publisher/modelKey 的限定形态”与 source key 比较。
    /// </summary>
    private static bool SourceMatches(string sourceKey, string? publisher, string? modelKey, string? path, string? indexedModelIdentifier)
    {
        if (string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(modelKey))
        {
            return false;
        }

        if (IsGgufSourcePath(sourceKey))
        {
            string normalizedSource = NormalizeModelIdentifierPath(sourceKey);
            string? sourcePublisher = normalizedSource.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.Equals(sourcePublisher, publisher, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? indexedPath = ExtractIndexedRelativePath(indexedModelIdentifier);
            if (!string.IsNullOrWhiteSpace(indexedModelIdentifier) && string.IsNullOrWhiteSpace(indexedPath))
            {
                return false;
            }

            string[] evidencePaths = [.. new[] { path, indexedPath }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()];
            return evidencePaths.Length > 0 && evidencePaths.All(value => normalizedSource.Equals(NormalizeModelIdentifierPath(value), StringComparison.OrdinalIgnoreCase));
        }

        string normalizedSourceId = NormalizeModelIdentifierPath(sourceKey);
        string normalizedModelKey = NormalizeModelIdentifierPath(modelKey);
        string normalizedPublisher = publisher.Trim().Trim('/');
        string qualified = normalizedModelKey.StartsWith(normalizedPublisher + "/", StringComparison.OrdinalIgnoreCase)
            ? normalizedModelKey
            : normalizedPublisher + "/" + normalizedModelKey;
        return normalizedSourceId.Contains('/', StringComparison.Ordinal)
            ? normalizedSourceId.Equals(qualified, StringComparison.OrdinalIgnoreCase)
            : normalizedSourceId.Equals(normalizedModelKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>modelKey 一致性判定：等于 loaded ID，或归一化后等于 source key。</summary>
    private static bool ModelKeyMatches(string? modelKey, string loadedId, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return false;
        }

        return modelKey.Equals(loadedId, StringComparison.OrdinalIgnoreCase) ||
            NormalizeModelIdentifierPath(modelKey).Equals(NormalizeModelIdentifierPath(sourceKey), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断 source key 是否为 .gguf 路径形态。</summary>
    private static bool IsGgufSourcePath(string sourceKey) =>
        sourceKey.Trim().EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>归一化模型标识路径：反斜杠统一为正斜杠，去掉前导 “./”。</summary>
    private static string NormalizeModelIdentifierPath(string value)
    {
        string normalized = value.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    /// <summary>从 lms ls 条目提取候选：按选中变体（或 source key）过滤，量化/架构宽松匹配。</summary>
    private static void AddLsCandidate(List<CliCandidate> candidates, JsonElement item, string sourceKey, string? selectedVariant, ModelProfile model, string source)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? modelKey = GetString(item, "modelKey");
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedVariant))
        {
            if (!StringEquals(modelKey, selectedVariant))
            {
                return;
            }
        }
        else if (!StringEquals(modelKey, sourceKey))
        {
            return;
        }

        string? quantization = GetQuantization(item);
        string? architecture = GetString(item, "architecture");
        if (!OptionalExact(model.Quantization, quantization) || !Compatible(model.Architecture, architecture))
        {
            return;
        }

        string? indexedPath = ExtractIndexedRelativePath(GetString(item, "indexedModelIdentifier"));
        candidates.Add(new CliCandidate(sourceKey, selectedVariant ?? (StringEquals(modelKey, sourceKey) ? null : modelKey), architecture, quantization, [indexedPath, GetString(item, "path")], source, null));
    }

    /// <summary>候选归并：逐个解析路径，按文件路径去重后唯一 → Unique，多个 → Ambiguous，零个按拒绝原因归类。</summary>
    private static EvidenceResult ResolveCandidates(IReadOnlyList<CliCandidate> candidates, IReadOnlyList<string> modelRoots)
    {
        if (candidates.Count == 0)
        {
            return new EvidenceResult(EvidenceStatus.NoMatch, null);
        }

        var rejections = new HashSet<PathEvidenceStatus>();
        List<LmStudioModelFileResolution> resolved = [];
        foreach (CliCandidate candidate in candidates)
        {
            PathResolution paths = ResolveCandidatePaths(candidate, modelRoots);
            foreach (PathEvidenceStatus rejection in paths.Rejections)
            {
                rejections.Add(rejection);
            }

            foreach (string path in paths.Paths)
            {
                string? concreteModelIdentifier = ResolveConcreteModelIdentifier(path, modelRoots);
                resolved.Add(new LmStudioModelFileResolution(path, candidate.SourceModelKey, candidate.SelectedVariant, candidate.Architecture, candidate.Quantization, candidate.Source, concreteModelIdentifier));
            }
        }

        LmStudioModelFileResolution[] unique = resolved
            .DistinctBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unique.Length == 1)
        {
            return new EvidenceResult(EvidenceStatus.Unique, unique[0]);
        }

        if (unique.Length > 1)
        {
            return new EvidenceResult(EvidenceStatus.Ambiguous, null);
        }

        EvidenceStatus status = rejections.Contains(PathEvidenceStatus.Unsafe)
            ? EvidenceStatus.UnsafePath
            : rejections.Contains(PathEvidenceStatus.UnsupportedFileType)
                ? EvidenceStatus.UnsupportedFileType
                : rejections.Contains(PathEvidenceStatus.Missing)
                    ? EvidenceStatus.MissingFile
                    : EvidenceStatus.NoMatch;
        return new EvidenceResult(status, null);
    }

    /// <summary>
    /// 解析单个候选的路径：绝对路径直接核验（在根内 + publisher 匹配 + 存在）；
    /// 相对路径在每个 models 根下拼接后同样核验，同时收集各类拒绝原因。
    /// </summary>
    private static PathResolution ResolveCandidatePaths(CliCandidate candidate, IReadOnlyList<string> modelRoots)
    {
        List<string> paths = [];
        var rejections = new HashSet<PathEvidenceStatus>();
        foreach (string rawPath in candidate.Paths.Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>())
        {
            if (!Path.GetExtension(rawPath).Equals(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add(PathEvidenceStatus.UnsupportedFileType);
                continue;
            }

            if (Path.IsPathFullyQualified(rawPath))
            {
                TryAddAbsolutePath(paths, rejections, rawPath, modelRoots, candidate.RequiredPublisher);
                continue;
            }

            bool withinRoot = false;
            bool exists = false;
            foreach (string root in modelRoots)
            {
                string candidatePath;
                try
                {
                    candidatePath = Path.GetFullPath(Path.Combine(root, rawPath.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    rejections.Add(PathEvidenceStatus.Unsafe);
                    continue;
                }

                if (!IsUnderRoot(candidatePath, root) || !PublisherPathMatches(candidatePath, root, candidate.RequiredPublisher))
                {
                    rejections.Add(PathEvidenceStatus.Unsafe);
                    continue;
                }

                withinRoot = true;
                if (File.Exists(candidatePath))
                {
                    exists = true;
                    paths.Add(candidatePath);
                }
            }

            if (withinRoot && !exists)
            {
                rejections.Add(PathEvidenceStatus.Missing);
            }
        }

        return new PathResolution(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), rejections);
    }

    /// <summary>从绝对路径反推 concrete model identifier（相对 models 根的归一化相对路径）；越出根或逃逸则返回 null。</summary>
    private static string? ResolveConcreteModelIdentifier(string path, IReadOnlyList<string> modelRoots)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = modelRoots.FirstOrDefault(candidateRoot => IsUnderRoot(fullPath, candidateRoot));
        if (root is null)
        {
            return null;
        }

        string relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeModelIdentifierPath(relative);
    }

    /// <summary>核验并登记一个绝对路径候选：必须在某个 models 根内且 publisher 匹配，文件须存在。</summary>
    private static void TryAddAbsolutePath(List<string> paths, HashSet<PathEvidenceStatus> rejections, string rawPath, IReadOnlyList<string> modelRoots, string? requiredPublisher)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            rejections.Add(PathEvidenceStatus.Unsafe);
            return;
        }

        string? root = modelRoots.FirstOrDefault(candidateRoot =>
            IsUnderRoot(fullPath, candidateRoot) && PublisherPathMatches(fullPath, candidateRoot, requiredPublisher));
        if (root is null)
        {
            rejections.Add(PathEvidenceStatus.Unsafe);
        }
        else if (File.Exists(fullPath))
        {
            paths.Add(fullPath);
        }
        else
        {
            rejections.Add(PathEvidenceStatus.Missing);
        }
    }

    /// <summary>校验路径相对根的首段目录是否为要求的 publisher（未要求时恒通过）。</summary>
    private static bool PublisherPathMatches(string path, string root, string? requiredPublisher)
    {
        if (string.IsNullOrWhiteSpace(requiredPublisher))
        {
            return true;
        }

        string relative = Path.GetRelativePath(root, path);
        string? firstSegment = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(firstSegment, requiredPublisher, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断路径是否位于根目录之下（忽略大小写）。</summary>
    private static bool IsUnderRoot(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取 models 根目录：settings.json 的 downloadsFolder（必须绝对路径）优先，默认 ~/.lmstudio/models 兜底。</summary>
    private static string[] ReadModelRoots(string? settingsJson, string userProfile)
    {
        string fullProfile = Path.GetFullPath(userProfile);
        List<string> roots = [];
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            using JsonDocument document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("settings root must be an object");
            }

            if (document.RootElement.TryGetProperty("downloadsFolder", out JsonElement downloadsFolder) &&
                downloadsFolder.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new JsonException("downloadsFolder must be a string or null");
            }

            string? configured = GetString(document.RootElement, "downloadsFolder");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathFullyQualified(configured))
                {
                    throw new InvalidDataException("downloadsFolder must be absolute");
                }

                roots.Add(Path.GetFullPath(configured));
            }
        }

        roots.Add(Path.GetFullPath(Path.Combine(fullProfile, ".lmstudio", "models")));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>解析用户目录：优先 USERPROFILE 环境变量（须为绝对路径），否则已知文件夹 API。</summary>
    private static string ResolveUserProfile()
    {
        string? environmentProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(environmentProfile) && Path.IsPathFullyQualified(environmentProfile))
        {
            return Path.GetFullPath(environmentProfile);
        }

        return Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>从 indexedModelIdentifier 提取“@”之后的 .gguf 相对路径；形态不符返回 null。</summary>
    private static string? ExtractIndexedRelativePath(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        int separator = identifier.IndexOf('@');
        string relative = separator >= 0 ? identifier[(separator + 1)..] : identifier;
        return relative.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? relative : null;
    }

    /// <summary>组装成功结果（附证据来源说明）。</summary>
    private static LmStudioModelFileResolutionAttempt Success(LmStudioModelFileResolution resolution) => new(
        LmStudioModelFileResolutionStatus.Success,
        resolution,
        $"已通过 {resolution.Source} 唯一解析 GGUF，并保持 native loaded instance 为权威状态来源。");

    /// <summary>组装失败结果（附诊断说明）。</summary>
    private static LmStudioModelFileResolutionAttempt Failure(LmStudioModelFileResolutionStatus status, string diagnostic) => new(status, null, diagnostic);

    /// <summary>宽松匹配：期望缺失视为匹配；两侧都有值时忽略大小写相等。</summary>
    private static bool Compatible(string? expected, string? actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        !string.IsNullOrWhiteSpace(actual) && expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>精确匹配：两侧都必须有值且忽略大小写相等。</summary>
    private static bool Exact(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) &&
        !string.IsNullOrWhiteSpace(actual) &&
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>可选精确匹配：一侧缺失则要求另一侧也缺失；两侧都有值时忽略大小写相等。</summary>
    private static bool OptionalExact(string? expected, string? actual)
    {
        bool expectedMissing = string.IsNullOrWhiteSpace(expected);
        bool actualMissing = string.IsNullOrWhiteSpace(actual);
        return expectedMissing || actualMissing
            ? expectedMissing && actualMissing
            : expected!.Equals(actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>忽略大小写的字符串相等。</summary>
    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>读取字符串属性；不存在或类型不符返回 null。</summary>
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>读取 int 属性；不存在或无法转换返回 null。</summary>
    private static int? GetInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : null;

    /// <summary>读取 quantization 字段：兼容字符串与 { name: ... } 对象两种形态。</summary>
    private static string? GetQuantization(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("quantization", out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => GetString(value, "name"),
            _ => null,
        };
    }

    /// <summary>单条证据链的解析状态。</summary>
    private enum EvidenceStatus
    {
        Unique,
        NoMatch,
        IdentityMismatch,
        UnsafePath,
        MissingFile,
        UnsupportedFileType,
        Ambiguous,
        InvalidJson,
        CommandFailed
    }

    /// <summary>路径级拒绝原因。</summary>
    private enum PathEvidenceStatus
    {
        Unsafe,
        Missing,
        UnsupportedFileType
    }

    /// <summary>证据链解析结果：状态、定位结果（成功时）与诊断。</summary>
    private sealed record EvidenceResult(
        EvidenceStatus Status,
        LmStudioModelFileResolution? Resolution,
        string? Diagnostic = null);

    /// <summary>单个候选的路径解析结果。</summary>
    private sealed record PathResolution(
        IReadOnlyList<string> Paths,
        IReadOnlySet<PathEvidenceStatus> Rejections);

    /// <summary>CLI 候选条目：source key、变体、架构/量化、候选路径、证据来源与要求的 publisher。</summary>
    private sealed record CliCandidate(
        string SourceModelKey,
        string? SelectedVariant,
        string? Architecture,
        string? Quantization,
        IReadOnlyList<string?> Paths,
        string Source,
        string? RequiredPublisher);

    /// <summary>构造 lms CLI 子进程启动信息（隐藏窗口、UTF-8 重定向、逐参数传递）。</summary>
    internal static ProcessStartInfo CreateLmsProcessStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>真实进程版 lms CLI 运行器：固定查 ~/.lmstudio/bin 下的 lms，找不到退回 PATH 查找。</summary>
    private sealed class ProcessLmsCliCommandRunner : ILmsCliCommandRunner
    {
        /// <summary>运行 lms 命令并归并结果状态；退出码非 0 或无输出视为失败。</summary>
        public async Task<LmsCliCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string executable = FindLmsExecutable() ?? (OperatingSystem.IsWindows() ? "lms.exe" : "lms");
            ProcessStartInfo startInfo = CreateLmsProcessStartInfo(executable, arguments);
            try
            {
                BoundedProcessResult result = await BoundedProcessRunner.RunAsync(startInfo, timeout, BoundedProcessRunner.CatalogOutputLimit, BoundedProcessRunner.CatalogOutputLimit, cancellationToken, combineOutputBudget: true).ConfigureAwait(false);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    return new LmsCliCommandResult(LmsCliCommandStatus.Failed, null);
                }

                return new LmsCliCommandResult(LmsCliCommandStatus.Success, result.StandardOutput);
            }
            catch (Win32Exception)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.Unavailable, null);
            }
            catch (ProcessOutputLimitException)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.OutputTooLarge, null);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.Failed, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.TimedOut, null);
            }
        }

        /// <summary>定位 ~/.lmstudio/bin 下的 lms 可执行文件。</summary>
        private static string? FindLmsExecutable()
        {
            string? profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(profile))
            {
                string installed = Path.Combine(profile, ".lmstudio", "bin", OperatingSystem.IsWindows() ? "lms.exe" : "lms");
                if (File.Exists(installed))
                {
                    return installed;
                }
            }

            return null;
        }
    }
}

/// <summary>lms CLI 命令的执行状态。</summary>
internal enum LmsCliCommandStatus
{
    Success,
    Unavailable,
    TimedOut,
    Failed,
    OutputTooLarge
}

/// <summary>lms CLI 命令结果：状态与标准输出（失败时为 null）。</summary>
internal sealed record LmsCliCommandResult(
    LmsCliCommandStatus Status,
    string? StandardOutput);

/// <summary>lms CLI 运行器抽象（便于测试注入）。</summary>
internal interface ILmsCliCommandRunner
{
    /// <summary>运行一次 lms 命令并返回归并后的结果。</summary>
    Task<LmsCliCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}
