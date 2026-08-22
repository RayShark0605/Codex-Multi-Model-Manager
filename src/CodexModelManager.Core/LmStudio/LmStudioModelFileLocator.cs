using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed class LmStudioModelFileLocator : ILmStudioModelFileLocator
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;
    private static readonly Uri DefaultEndpoint = new("http://127.0.0.1:1234");
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    private readonly ILmsCliCommandRunner commandRunner;
    private readonly Func<string> userProfileProvider;

    public LmStudioModelFileLocator()
        : this(new ProcessLmsCliCommandRunner(), ResolveUserProfile)
    {
    }

    internal LmStudioModelFileLocator(
        ILmsCliCommandRunner commandRunner,
        Func<string> userProfileProvider)
    {
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.userProfileProvider = userProfileProvider ?? throw new ArgumentNullException(nameof(userProfileProvider));
    }

    public async Task<LmStudioModelFileResolutionAttempt> ResolveAsync(
        ModelProfile model,
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!HasAuthoritativeLoadedIdentity(model))
        {
            return Failure(
                LmStudioModelFileResolutionStatus.InvalidModelSnapshot,
                "native loaded instance 快照缺少 loaded ID、source、type、architecture、quantization 或实际 context；拒绝猜测 GGUF。");
        }

        try
        {
            LmStudioEndpointPolicy.Validate(endpoint);
        }
        catch (InvalidOperationException)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.UnsupportedEndpoint,
                "LM Studio endpoint 不满足安全 URI 约束；自动 GGUF 定位已阻断。");
        }

        if (!endpoint.IsLoopback)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.UnsupportedEndpoint,
                "lms ps 返回的是服务端文件路径；仅本机 loopback endpoint 允许自动定位 GGUF。");
        }

        string userProfile;
        IReadOnlyList<string> modelRoots;
        try
        {
            userProfile = Path.GetFullPath(userProfileProvider());
            string settingsPath = Path.Combine(userProfile, ".lmstudio", "settings.json");
            string? settingsJson = File.Exists(settingsPath)
                ? await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false)
                : null;
            modelRoots = ReadModelRoots(settingsJson, userProfile);
        }
        catch (JsonException)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.InvalidSettings,
                "LM Studio settings.json 不是有效 JSON；拒绝推断 models 根目录。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.InvalidSettings,
                "无法安全读取或规范化 LM Studio models 根目录；自动定位已阻断。");
        }

        LmsCliCommandResult variantsCommand = await commandRunner.RunAsync(
            ["ls", "--json", "--variants"],
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (variantsCommand.Status == LmsCliCommandStatus.Unavailable)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.CliUnavailable,
                "未找到或无法启动 lms CLI；请检查 LM Studio CLI 安装，或手工选择 GGUF。");
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

        EvidenceResult variants = ParseCommand(
            variantsCommand,
            json => ResolveLsEvidence(model, json, modelRoots));
        EvidenceResult processes = ParseCommand(
            processesCommand,
            json => ResolvePsEvidence(model, json, modelRoots));

        LmStudioModelFileResolutionAttempt? blocking = ResolveBlockingFailure(variants, processes);
        if (blocking is not null)
        {
            return blocking;
        }

        if (variants.Resolution is not null && processes.Resolution is not null)
        {
            if (!variants.Resolution.FilePath.Equals(processes.Resolution.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    LmStudioModelFileResolutionStatus.Conflict,
                    "lms ls 与 lms ps 分别解析到不同的有效 GGUF 路径；拒绝自动选择，请手工核对。");
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

    public static async Task<string?> TryResolveAsync(
        ModelProfile model,
        CancellationToken cancellationToken = default) =>
        (await TryResolveDetailedAsync(model, cancellationToken).ConfigureAwait(false))?.FilePath;

    public static async Task<LmStudioModelFileResolution?> TryResolveDetailedAsync(
        ModelProfile model,
        CancellationToken cancellationToken = default)
    {
        LmStudioModelFileResolutionAttempt attempt = await new LmStudioModelFileLocator()
            .ResolveAsync(model, DefaultEndpoint, cancellationToken)
            .ConfigureAwait(false);
        return attempt.Resolution;
    }

    public static LmStudioModelFileResolution? ResolveFromJson(
        ModelProfile model,
        string variantsJson,
        string? settingsJson,
        string userProfile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        EvidenceResult result = ResolveLsEvidence(model, variantsJson, ReadModelRoots(settingsJson, userProfile));
        return result.Resolution;
    }

    internal static LmStudioModelFileResolutionAttempt ResolvePsFromJson(
        ModelProfile model,
        string processesJson,
        string? settingsJson,
        string userProfile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(processesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        try
        {
            EvidenceResult result = ResolvePsEvidence(model, processesJson, ReadModelRoots(settingsJson, userProfile));
            return result.Resolution is not null
                ? Success(result.Resolution)
                : Failure(MapEvidenceStatus(result.Status), DiagnosticForEvidence(result.Status));
        }
        catch (JsonException)
        {
            return Failure(LmStudioModelFileResolutionStatus.InvalidJson, "lms ps --json 输出不是有效 JSON；自动定位已阻断。");
        }
    }

    private static bool HasAuthoritativeLoadedIdentity(ModelProfile model) =>
        model.Provider == ProviderKind.LmStudio &&
        model.IsLoaded == true &&
        !string.IsNullOrWhiteSpace(model.LoadedInstanceId ?? model.Id) &&
        !string.IsNullOrWhiteSpace(model.SourceModelKey) &&
        !string.IsNullOrWhiteSpace(model.ModelType) &&
        !string.IsNullOrWhiteSpace(model.Architecture) &&
        !string.IsNullOrWhiteSpace(model.Quantization) &&
        model.LoadedContextLength is > 0;

    private static EvidenceResult ParseCommand(
        LmsCliCommandResult command,
        Func<string, EvidenceResult> parser)
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

    private static LmStudioModelFileResolutionAttempt? ResolveCommandFailure(
        LmsCliCommandResult variantsCommand,
        LmsCliCommandResult processesCommand)
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

    private static LmStudioModelFileResolutionAttempt? ResolveBlockingFailure(
        EvidenceResult variants,
        EvidenceResult processes)
    {
        if (variants.Status == EvidenceStatus.InvalidJson || processes.Status == EvidenceStatus.InvalidJson)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.InvalidJson,
                "lms CLI 返回了非法 JSON；为避免忽略冲突证据，自动定位已阻断。");
        }

        if (variants.Status == EvidenceStatus.Ambiguous || processes.Status == EvidenceStatus.Ambiguous)
        {
            return Failure(
                LmStudioModelFileResolutionStatus.Ambiguous,
                "lms CLI 对同一 native loaded instance 给出多个有效 GGUF 候选；拒绝自动选择。");
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
            if (variants.Status == status || processes.Status == status)
            {
                return Failure(MapEvidenceStatus(status), DiagnosticForEvidence(status));
            }
        }

        return null;
    }

    private static LmStudioModelFileResolutionAttempt ResolveNoMatchFailure(
        LmsCliCommandResult variantsCommand,
        LmsCliCommandResult processesCommand,
        EvidenceResult variants,
        EvidenceResult processes)
    {
        EvidenceStatus evidenceStatus = HighestPriorityEvidenceStatus(variants.Status, processes.Status);
        if (evidenceStatus is not (EvidenceStatus.NoMatch or EvidenceStatus.CommandFailed))
        {
            return Failure(MapEvidenceStatus(evidenceStatus), DiagnosticForEvidence(evidenceStatus));
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
    private static EvidenceResult ResolveLsEvidence(
        ModelProfile model,
        string variantsJson,
        IReadOnlyList<string> modelRoots)
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

    private static EvidenceResult ResolvePsEvidence(
        ModelProfile model,
        string processesJson,
        IReadOnlyList<string> modelRoots)
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
            bool instanceMatches = StringEquals(modelKey, loadedId) && StringEquals(identifier, loadedId);
            bool sourceMatches = SourceMatches(sourceKey, publisher, modelKey);
            bool metadataMatches = Exact(model.ModelType, type) &&
                Exact(model.Architecture, architecture) &&
                Exact(model.Quantization, quantization) &&
                model.LoadedContextLength == contextLength &&
                string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase);
            if (!instanceMatches || !sourceMatches || !metadataMatches || string.IsNullOrWhiteSpace(publisher))
            {
                sawIdentityMismatch = true;
                continue;
            }

            candidates.Add(new CliCandidate(
                sourceKey,
                model.SelectedVariant,
                architecture,
                quantization,
                [GetString(item, "path")],
                "lms ps --json",
                publisher));
        }

        if (candidates.Count == 0)
        {
            return new EvidenceResult(
                sawObject && sawIdentityMismatch ? EvidenceStatus.IdentityMismatch : EvidenceStatus.NoMatch,
                null);
        }

        return ResolveCandidates(candidates, modelRoots);
    }

    private static bool SourceMatches(string sourceKey, string? publisher, string? modelKey)
    {
        if (string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(modelKey))
        {
            return false;
        }

        string qualified = publisher.TrimEnd('/') + "/" + modelKey.TrimStart('/');
        return sourceKey.Contains('/', StringComparison.Ordinal)
            ? sourceKey.Equals(qualified, StringComparison.OrdinalIgnoreCase)
            : sourceKey.Equals(modelKey, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddLsCandidate(
        List<CliCandidate> candidates,
        JsonElement item,
        string sourceKey,
        string? selectedVariant,
        ModelProfile model,
        string source)
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
        if (!Compatible(model.Quantization, quantization) || !Compatible(model.Architecture, architecture))
        {
            return;
        }

        string? indexedPath = ExtractIndexedRelativePath(GetString(item, "indexedModelIdentifier"));
        candidates.Add(new CliCandidate(
            sourceKey,
            selectedVariant ?? (StringEquals(modelKey, sourceKey) ? null : modelKey),
            architecture,
            quantization,
            [indexedPath, GetString(item, "path")],
            source,
            null));
    }

    private static EvidenceResult ResolveCandidates(
        IReadOnlyList<CliCandidate> candidates,
        IReadOnlyList<string> modelRoots)
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
                resolved.Add(new LmStudioModelFileResolution(
                    path,
                    candidate.SourceModelKey,
                    candidate.SelectedVariant,
                    candidate.Architecture,
                    candidate.Quantization,
                    candidate.Source));
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

    private static PathResolution ResolveCandidatePaths(
        CliCandidate candidate,
        IReadOnlyList<string> modelRoots)
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
    private static void TryAddAbsolutePath(
        List<string> paths,
        HashSet<PathEvidenceStatus> rejections,
        string rawPath,
        IReadOnlyList<string> modelRoots,
        string? requiredPublisher)
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

    private static bool IsUnderRoot(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

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

    private static string ResolveUserProfile()
    {
        string? environmentProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(environmentProfile) && Path.IsPathFullyQualified(environmentProfile))
        {
            return Path.GetFullPath(environmentProfile);
        }

        return Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

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

    private static LmStudioModelFileResolutionAttempt Success(LmStudioModelFileResolution resolution) => new(
        LmStudioModelFileResolutionStatus.Success,
        resolution,
        $"已通过 {resolution.Source} 唯一解析 GGUF，并保持 native loaded instance 为权威状态来源。");

    private static LmStudioModelFileResolutionAttempt Failure(
        LmStudioModelFileResolutionStatus status,
        string diagnostic) => new(status, null, diagnostic);

    private static bool Compatible(string? expected, string? actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        !string.IsNullOrWhiteSpace(actual) && expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    private static bool Exact(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) &&
        !string.IsNullOrWhiteSpace(actual) &&
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;

    private static string? GetQuantization(JsonElement element)
    {
        if (!element.TryGetProperty("quantization", out JsonElement value))
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

    private enum PathEvidenceStatus
    {
        Unsafe,
        Missing,
        UnsupportedFileType
    }

    private sealed record EvidenceResult(
        EvidenceStatus Status,
        LmStudioModelFileResolution? Resolution);

    private sealed record PathResolution(
        IReadOnlyList<string> Paths,
        IReadOnlySet<PathEvidenceStatus> Rejections);

    private sealed record CliCandidate(
        string SourceModelKey,
        string? SelectedVariant,
        string? Architecture,
        string? Quantization,
        IReadOnlyList<string?> Paths,
        string Source,
        string? RequiredPublisher);

    private sealed class ProcessLmsCliCommandRunner : ILmsCliCommandRunner
    {
        public async Task<LmsCliCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            string executable = FindLmsExecutable() ?? (OperatingSystem.IsWindows() ? "lms.exe" : "lms");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    return new LmsCliCommandResult(LmsCliCommandStatus.Failed, null);
                }
            }
            catch (Win32Exception)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.Unavailable, null);
            }
            catch (IOException)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.Failed, null);
            }

            process.StandardInput.Close();
            Task<BoundedOutput> stdoutTask = ReadBoundedOutputAsync(process.StandardOutput);
            Task stderrTask = DrainOutputAsync(process.StandardError);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                await WaitForExitAfterTerminationAsync(process).ConfigureAwait(false);
                await ObserveOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new LmsCliCommandResult(LmsCliCommandStatus.TimedOut, null);
            }

            BoundedOutput output;
            try
            {
                output = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
            }
            catch (IOException)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.Failed, null);
            }

            if (process.ExitCode != 0)
            {
                return new LmsCliCommandResult(LmsCliCommandStatus.Failed, null);
            }

            if (output.TooLarge || output.Text.Length == 0)
            {
                return new LmsCliCommandResult(
                    output.TooLarge ? LmsCliCommandStatus.OutputTooLarge : LmsCliCommandStatus.Failed,
                    null);
            }

            return new LmsCliCommandResult(LmsCliCommandStatus.Success, output.Text);
        }

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

        private static void TryTerminate(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
        }

        private static async Task WaitForExitAfterTerminationAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static async Task<BoundedOutput> ReadBoundedOutputAsync(StreamReader reader)
        {
            var output = new StringBuilder();
            char[] buffer = new char[8192];
            bool tooLarge = false;
            int charactersRead;
            while ((charactersRead = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                int remaining = MaximumOutputCharacters - output.Length;
                if (remaining > 0)
                {
                    output.Append(buffer, 0, Math.Min(remaining, charactersRead));
                }

                if (charactersRead > remaining)
                {
                    tooLarge = true;
                }
            }

            return new BoundedOutput(output.ToString(), tooLarge);
        }

        private static async Task DrainOutputAsync(StreamReader reader)
        {
            char[] buffer = new char[8192];
            while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) > 0)
            {
            }
        }

        private static async Task ObserveOutputAsync(params Task[] tasks)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        private sealed record BoundedOutput(string Text, bool TooLarge);
    }
}

internal enum LmsCliCommandStatus
{
    Success,
    Unavailable,
    TimedOut,
    Failed,
    OutputTooLarge
}

internal sealed record LmsCliCommandResult(
    LmsCliCommandStatus Status,
    string? StandardOutput);

internal interface ILmsCliCommandRunner
{
    Task<LmsCliCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
