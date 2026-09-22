using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// 配置切换服务：生成并提交 Codex 配置切换计划。
/// 预览阶段读取当前配置与设置、按目标 Provider 组装候选并做语义校验；
/// 提交阶段重复全部实时校验（含计划哈希比对），先创建备份快照，再以原子批量写入落地，
/// 同时维护 Provider 状态快照与 Secondary Override 原始值记录。
/// </summary>
public sealed class ConfigurationSwitchService
{
    private readonly ICodexHomeProvider homeProvider;
    private readonly IConfigPatchEngine patchEngine;
    private readonly IAtomicBatchWriter writer;
    private readonly IBackupService backups;
    private readonly ISecondaryModelOverrideScanner overrideScanner;
    private readonly ICodexRuntimeProbe runtimeProbe;
    private readonly AppSettingsRepository settingsRepository;
    private readonly ISecretStore secretStore;
    private readonly ILmStudioSwitchPreflight lmStudioPreflight;

    /// <summary>注入全部协作者构造切换服务。</summary>
    public ConfigurationSwitchService(
        ICodexHomeProvider homeProvider,
        IConfigPatchEngine patchEngine,
        IAtomicBatchWriter writer,
        IBackupService backups,
        ISecondaryModelOverrideScanner overrideScanner,
        ICodexRuntimeProbe runtimeProbe,
        AppSettingsRepository settingsRepository,
        ISecretStore secretStore,
        ILmStudioSwitchPreflight lmStudioPreflight)
    {
        this.homeProvider = homeProvider;
        this.patchEngine = patchEngine;
        this.writer = writer;
        this.backups = backups;
        this.overrideScanner = overrideScanner;
        this.runtimeProbe = runtimeProbe;
        this.settingsRepository = settingsRepository;
        this.secretStore = secretStore;
        this.lmStudioPreflight = lmStudioPreflight;
    }

    /// <summary>自动压缩阈值的当前策略版本（用于迁移旧设置）。</summary>
    public const int AutoCompactPolicyVersion = 2;

    /// <summary>
    /// 建议的自动压缩阈值：取“上下文窗口 80%”与“窗口减去保留量（至多 24K、且不超过窗口一半）”的较小者。
    /// </summary>
    public static int SuggestAutoCompact(int contextWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextWindow, 2_048);
        const int absoluteReserve = 24_576;
        const double maximumUsageRatio = 0.80;
        int proportionalLimit = (int)Math.Floor(contextWindow * maximumUsageRatio);
        int boundedAbsoluteReserve = Math.Min(absoluteReserve, contextWindow / 2);
        return Math.Min(proportionalLimit, contextWindow - boundedAbsoluteReserve);
    }

    /// <summary>建议的工具输出上限：自适应值（窗口/50，夹在 2K~4K）与压缩阈值四分之一的较小者（不低于 256）。</summary>
    public static int SuggestToolOutputLimit(int contextWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextWindow, 2_048);
        int adaptiveLimit = Math.Clamp(contextWindow / 50, 2_048, 4_096);
        int compactLimit = SuggestAutoCompact(contextWindow);
        int smallContextLimit = Math.Max(256, compactLimit / 4);
        return Math.Min(adaptiveLimit, smallContextLimit);
    }

    /// <summary>
    /// 解析自动压缩偏好：同一上下文窗口下已记录 Manual 且阈值合法时沿用用户手填值，
    /// 否则回退到当前建议值。
    /// </summary>
    public static (int Limit, AutoCompactMode Mode) ResolveAutoCompactPreference(ModelPreference? preference, int contextWindow)
    {
        int suggestedCompact = SuggestAutoCompact(contextWindow);
        if (preference?.LastLoadedContext == contextWindow &&
            preference.AutoCompactMode == AutoCompactMode.Manual &&
            preference.AutoCompactTokenLimit is int manualCompact &&
            manualCompact > 0 && manualCompact < contextWindow && contextWindow - manualCompact >= 1_024)
        {
            return (manualCompact, AutoCompactMode.Manual);
        }

        return (suggestedCompact, AutoCompactMode.Automatic);
    }

    /// <summary>旧版（v1 策略）的自动压缩建议值，仅用于迁移时识别历史 Automatic 记录。</summary>
    internal static int SuggestLegacyAutoCompact(int contextWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextWindow, 2_048);
        return Math.Min((int)Math.Floor(contextWindow * 0.90), contextWindow - 8_192);
    }

    /// <summary>生成切换计划：规范化请求、执行 LM Studio 前置探测并构建完整计划（含确认校验）。</summary>
    public async Task<SwitchPlan> CreatePlanAsync(SwitchRequest request, CancellationToken cancellationToken = default)
    {
        SwitchRequest effectiveRequest = NormalizeSwitchRequest(request);
        CodexInstructionHierarchyProbeResult? preflight = await EnsureLmStudioPreflightAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
        return await CreatePlanCoreAsync(effectiveRequest, preflight, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 仅供“组合修复确认”流程生成修复预览；这不代表授权提交——
    /// CommitAsync 仍会对该计划重复执行全部实时兼容门禁。
    /// </summary>
    internal Task<SwitchPlan> CreateRepairPreviewAsync(SwitchRequest request, CodexInstructionHierarchyProbeResult failure, CancellationToken cancellationToken = default)
    {
        if (request.TargetProvider != ProviderKind.LmStudio || failure.IsCompatible || failure.FailureCode is not
            (CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder))
        {
            throw new InvalidOperationException("只有待修复的 LM Studio 请求可以生成修复预览。");
        }

        return CreatePlanCoreAsync(NormalizeSwitchRequest(request), failure, cancellationToken);
    }

    /// <summary>计划构建核心：读取现状、按 Provider 组装候选、执行补丁与 Secondary Override 改写并全量校验。</summary>
    private async Task<SwitchPlan> CreatePlanCoreAsync(SwitchRequest request, CodexInstructionHierarchyProbeResult? preflight, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetModel);
        string configPath = Path.Combine(homeProvider.GetCodexHome(), "config.toml");
        TextFileSnapshot source = await TextFileCodec.ReadAsync(configPath, cancellationToken).ConfigureAwait(false);
        patchEngine.Validate(source.Text);
        ConfigReadResult read = patchEngine.Read(source.Text);
        ProviderKind sourceProvider = CodexRuntimeProbe.ParseProvider(CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model_provider")) ?? "openai");
        string? sourceModel = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model"));
        string settingsPath = Path.GetFullPath(settingsRepository.SettingsPath);
        FileFingerprint settingsFingerprint = await FileFingerprintService.CaptureAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        AppSettings settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!FileFingerprintService.Matches(settingsFingerprint, await FileFingerprintService.CaptureAsync(settingsPath, cancellationToken).ConfigureAwait(false)))
        {
            throw new IOException("生成预览期间应用设置发生变化，请重新预览。");
        }

        // 预览阶段的全部读取指纹：config、设置以及后续每个外部 override 文件
        Dictionary<string, FileFingerprint> readFingerprints = new(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(configPath)] = source.Fingerprint,
            [settingsPath] = settingsFingerprint,
        };
        string? catalogPath = request.TargetProvider switch
        {
            ProviderKind.DeepSeek => request.DeepSeekCatalogPath,
            ProviderKind.GLM => request.GlmCatalogPath,
            _ => null,
        };
        FileFingerprint? catalogFingerprint = string.IsNullOrWhiteSpace(catalogPath)
            ? null
            : await FileFingerprintService.CaptureAsync(Path.GetFullPath(catalogPath), cancellationToken).ConfigureAwait(false);
        List<string> warnings = [];
        IReadOnlyList<SecondaryModelOverride> overrides = await overrideScanner.ScanAsync(configPath, cancellationToken).ConfigureAwait(false);

        Dictionary<string, string?> roots = ManagedConfigKeys.Root.ToDictionary(key => key, _ => (string?)null, StringComparer.Ordinal);
        Dictionary<string, string?> tables = new(StringComparer.Ordinal);
        List<string> removeTables = ManagedConfigKeys.ProviderTables.ToList();
        switch (request.TargetProvider)
        {
            case ProviderKind.OpenAI:
                ConfigureOpenAi(roots, tables, removeTables, read, settings, request, warnings);
                break;
            case ProviderKind.DeepSeek:
                await ConfigureDeepSeekAsync(roots, tables, removeTables, read, settings, request, warnings, cancellationToken).ConfigureAwait(false);
                break;
            case ProviderKind.GLM:
                await ConfigureGlmAsync(roots, tables, removeTables, read, settings, request, warnings, cancellationToken).ConfigureAwait(false);
                break;
            case ProviderKind.LmStudio:
                ConfigureLmStudio(roots, tables, removeTables, read, request, warnings);
                break;
            default:
                throw new InvalidOperationException("unknown provider：已拒绝生成切换计划。");
        }

        if (catalogPath is not null && catalogFingerprint is FileFingerprint expectedCatalogFingerprint)
        {
            string fullCatalogPath = Path.GetFullPath(catalogPath);
            FileFingerprint actualCatalogFingerprint = await FileFingerprintService.CaptureAsync(fullCatalogPath, cancellationToken).ConfigureAwait(false);
            if (!FileFingerprintService.Matches(expectedCatalogFingerprint, actualCatalogFingerprint))
            {
                throw new IOException("Provider catalog 在生成预览期间发生变化，请重新加载并再次预览。");
            }

            readFingerprints[fullCatalogPath] = expectedCatalogFingerprint;
        }

        ConfigPatchResult patch = patchEngine.Apply(source.Text, new ConfigPatchRequest(roots, tables, removeTables));
        string candidateText = patch.Text;
        List<ConfigMutation> allMutations = [.. patch.Mutations];
        Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> secondaryReplacements = BuildSecondaryReplacements(request, overrides, settings, warnings);
        secondaryReplacements.TryGetValue(Path.GetFullPath(configPath), out Dictionary<string, SecondaryOverrideReplacement>? primaryReplacements);
        (candidateText, IReadOnlyList<ConfigMutation> secondaryMutations) = SecondaryOverridePatcher.Apply(candidateText, primaryReplacements ?? new Dictionary<string, SecondaryOverrideReplacement>());
        allMutations.AddRange(secondaryMutations);
        patchEngine.Validate(candidateText);

        // 候选语义校验 + 保留区计数校验，确保补丁没有误伤未纳管内容
        ConfigReadResult finalRead = patchEngine.Read(candidateText);
        ValidateCandidateSemantics(finalRead, request);
        if (finalRead.McpServerCount != read.McpServerCount || finalRead.ProjectCount != read.ProjectCount || finalRead.HookSectionCount != read.HookSectionCount || finalRead.PluginSectionCount != read.PluginSectionCount)
        {
            throw new InvalidDataException("保留区检查失败：MCP/Projects/Hooks/Plugins 数量发生意外变化。");
        }

        byte[] candidateBytes = TextFileCodec.Encode(candidateText, source.Format);
        List<PlannedFileChange> changes = [];
        changes.Add(new PlannedFileChange(
            configPath,
            source.Fingerprint,
            candidateBytes,
            [.. patch.Mutations, .. secondaryMutations],
            bytes =>
            {
                string decoded = DecodeUtf8(bytes);
                patchEngine.Validate(decoded);
                return ValueTask.CompletedTask;
            },
            CommitLast: true));

        // 外部 override 文件按路径序逐个生成计划项（仅当确有变更时）
        foreach ((string filePath, Dictionary<string, SecondaryOverrideReplacement> replacements) in secondaryReplacements.Where(pair => !pair.Key.Equals(Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextFileSnapshot external = await TextFileCodec.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (!external.Fingerprint.Exists)
            {
                throw new IOException($"选中的 Secondary Override 配置已不存在: {filePath}");
            }

            readFingerprints[Path.GetFullPath(filePath)] = external.Fingerprint;
            patchEngine.Validate(external.Text);
            (string externalCandidate, IReadOnlyList<ConfigMutation> externalMutations) = SecondaryOverridePatcher.Apply(external.Text, replacements);
            if (externalMutations.Count == 0)
            {
                continue;
            }

            patchEngine.Validate(externalCandidate);
            ConfigMutation[] taggedMutations = externalMutations
                .Select(mutation => mutation with { KeyPath = $"{filePath}::{mutation.KeyPath}" })
                .ToArray();
            allMutations.AddRange(taggedMutations);
            changes.Add(new PlannedFileChange(
                filePath,
                external.Fingerprint,
                TextFileCodec.Encode(externalCandidate, external.Format),
                taggedMutations,
                bytes =>
                {
                    patchEngine.Validate(DecodeUtf8(bytes));
                    return ValueTask.CompletedTask;
                }));
        }

        string planHash = ComputePlanHash(request, changes);
        HashSet<string>? explicitlySelected = ParseOverrideSelection(request.SecondaryOverrideSelectionJson);
        if (request.TargetProvider == ProviderKind.LmStudio && overrides.Any(item => item.IsPotentialCloudRequest &&
            !(request.SecondaryOverridePolicy == SecondaryOverridePolicy.FollowMain && IsOverrideSelected(item, explicitlySelected))))
        {
            warnings.Add("主模型将切换为本地模型，但 Secondary Model Overrides 仍可能访问云 Provider。");
        }

        SwitchPlan plan = new(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            request,
            sourceProvider,
            sourceModel,
            changes,
            allMutations,
            warnings,
            overrides,
            patch.Preservation,
            planHash,
            preflight)
        {
            ReadFingerprints = readFingerprints,
        };
        await VerifyConfirmationAsync(plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// 提交切换：确认无 Codex 进程运行后，重新执行预览全流程并比对计划哈希；
    /// 依次创建初始/补充基线与历史快照，更新设置（Provider 状态、override 原始值、
    /// LM Studio 模型偏好），最后把 config 变更与设置文件一起原子写入。
    /// </summary>
    public async Task CommitAsync(SwitchPlan preview, CancellationToken cancellationToken = default)
    {
        CodexEnvironmentInfo environment = await runtimeProbe.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (environment.IsRunning)
        {
            throw new InvalidOperationException("检测到 Codex/ChatGPT Desktop 或 codex 子进程仍在运行。请完全关闭后重新检测。");
        }

        await VerifyConfirmationAsync(preview, cancellationToken).ConfigureAwait(false);
        SwitchRequest effectiveRequest = NormalizeSwitchRequest(preview.Request);
        CodexInstructionHierarchyProbeResult? preflight = await EnsureLmStudioPreflightAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
        SwitchPlan regenerated = await CreatePlanCoreAsync(effectiveRequest, preflight, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.PlanHash, regenerated.PlanHash, StringComparison.Ordinal))
        {
            throw new IOException("配置文件在预览后发生变化，请重新加载并再次预览。");
        }

        await VerifyConfirmationAsync(preview, cancellationToken).ConfigureAwait(false);

        string configPath = Path.GetFullPath(Path.Combine(homeProvider.GetCodexHome(), "config.toml"));
        string[] supplementalFiles = regenerated.Files
            .Select(file => Path.GetFullPath(file.Path))
            .Where(path => !path.Equals(configPath, StringComparison.OrdinalIgnoreCase) && !path.Equals(Path.Combine(homeProvider.GetCodexHome(), "models.json"), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        await backups.EnsureInitialSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await backups.EnsureSupplementalBaselinesAsync(supplementalFiles, cancellationToken).ConfigureAwait(false);
        await backups.CreateHistorySnapshotAsync(
            BackupOperation.Switch,
            preview.SourceProvider.ToString(),
            preview.SourceModel,
            preview.Request.TargetProvider.ToString(),
            preview.Request.TargetModel,
            preview.Mutations.Select(item => item.KeyPath).ToArray(),
            supplementalFiles,
            cancellationToken).ConfigureAwait(false);

        // 提交前捕获来源 Provider 的状态快照与 Secondary Override 处置，写入应用设置
        PlannedFileChange configChange = regenerated.Files.Single(file => Path.GetFullPath(file.Path).Equals(configPath, StringComparison.OrdinalIgnoreCase));
        TextFileSnapshot before = await TextFileCodec.ReadAsync(configChange.Path, cancellationToken).ConfigureAwait(false);
        ConfigReadResult beforeRead = patchEngine.Read(before.Text);
        AppSettings settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (regenerated.SourceProvider is ProviderKind.OpenAI or ProviderKind.DeepSeek or ProviderKind.GLM)
        {
            settings.ProviderStates[regenerated.SourceProvider.ToString()] = CaptureProviderState(regenerated.SourceProvider, beforeRead, before.Fingerprint.Sha256);
        }

        if (regenerated.Request.SecondaryOverridePolicy == SecondaryOverridePolicy.FollowMain)
        {
            HashSet<string>? selected = ParseOverrideSelection(regenerated.Request.SecondaryOverrideSelectionJson);
            foreach (SecondaryModelOverride item in regenerated.SecondaryOverrides.Where(item => IsOverrideSelected(item, selected) && WasOverrideMutated(regenerated, item)))
            {
                settings.SecondaryOverrideOriginals.TryAdd(OverrideStateKey(item), EncodeSecondaryOriginal(item));
            }
        }
        else if (regenerated.Request.SecondaryOverridePolicy == SecondaryOverridePolicy.RestoreOriginal)
        {
            HashSet<string>? selected = ParseOverrideSelection(regenerated.Request.SecondaryOverrideSelectionJson);
            foreach (SecondaryModelOverride item in regenerated.SecondaryOverrides.Where(item => IsOverrideSelectedForRestore(item, selected, settings)))
            {
                settings.SecondaryOverrideOriginals.Remove(OverrideStateKey(item));
            }
        }

        settings.LastManagedConfigSha256 = Convert.ToHexString(SHA256.HashData(configChange.CandidateBytes ?? throw new InvalidDataException("切换计划缺少 config.toml 候选内容。")));
        settings.LastManagedAt = DateTimeOffset.Now;
        if (regenerated.Request.TargetProvider == ProviderKind.LmStudio && regenerated.Request.ContextWindow is int context)
        {
            settings.LmStudioEndpoint = regenerated.Request.LmStudioEndpoint?.AbsoluteUri.TrimEnd('/') ?? settings.LmStudioEndpoint;
            settings.ModelPreferences[regenerated.Request.TargetModel] = new ModelPreference
            {
                LastLoadedContext = context,
                CodexContext = context,
                AutoCompactTokenLimit = regenerated.Request.AutoCompactTokenLimit,
                AutoCompactMode = regenerated.Request.AutoCompactMode!.Value,
                AutoCompactPolicyVersion = AutoCompactPolicyVersion,
                ToolOutputTokenLimit = regenerated.Request.ToolOutputTokenLimit,
            };
        }

        // 设置文件也纳入同一原子事务：候选字节解析通过 JSON 校验后才写入
        byte[] settingsBytes = AppSettingsRepository.Serialize(settings);
        string settingsPath = settingsRepository.SettingsPath;
        FileFingerprint settingsFingerprint = regenerated.ReadFingerprints![Path.GetFullPath(settingsPath)];
        var settingsChange = new PlannedFileChange(
            settingsPath,
            settingsFingerprint,
            settingsBytes,
            [],
            bytes =>
            {
                using JsonDocument _ = JsonDocument.Parse(bytes);
                return ValueTask.CompletedTask;
            });
        await VerifyConfirmationAsync(regenerated, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync([.. regenerated.Files, settingsChange], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 确认校验：计划里的读取指纹与写入指纹必须自洽，
    /// 且每个涉及文件的当前指纹与预览时一致，否则要求重新预览。
    /// </summary>
    internal static async Task VerifyConfirmationAsync(SwitchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<string, FileFingerprint> fingerprints = new(StringComparer.OrdinalIgnoreCase);
        if (plan.ReadFingerprints is not null)
        {
            foreach ((string path, FileFingerprint fingerprint) in plan.ReadFingerprints)
            {
                fingerprints[Path.GetFullPath(path)] = fingerprint;
            }
        }

        foreach (PlannedFileChange file in plan.Files)
        {
            string path = Path.GetFullPath(file.Path);
            if (fingerprints.TryGetValue(path, out FileFingerprint? readFingerprint) && !FileFingerprintService.Matches(readFingerprint, file.ExpectedFingerprint))
            {
                throw new IOException("预览的读取指纹与写入指纹不一致，请重新预览。");
            }

            fingerprints[path] = file.ExpectedFingerprint;
        }

        foreach ((string path, FileFingerprint expected) in fingerprints)
        {
            FileFingerprint actual = await FileFingerprintService.CaptureAsync(path, cancellationToken).ConfigureAwait(false);
            if (!FileFingerprintService.Matches(expected, actual))
            {
                throw new IOException($"配置或应用设置在预览后发生变化，请重新预览: {Path.GetFileName(path)}");
            }
        }
    }

    /// <summary>组装切回 OpenAI 的配置：恢复历史状态或采用保守最小配置，绝不读写登录凭据。</summary>
    private static void ConfigureOpenAi(
        Dictionary<string, string?> roots,
        Dictionary<string, string?> tables,
        List<string> removeTables,
        ConfigReadResult read,
        AppSettings settings,
        SwitchRequest request,
        List<string> warnings)
    {
        if (settings.ProviderStates.TryGetValue(ProviderKind.OpenAI.ToString(), out ProviderState? state))
        {
            foreach (string key in ManagedConfigKeys.Root)
            {
                roots[key] = state.RootValues.GetValueOrDefault(key);
            }

            warnings.Add("将恢复上次由管理器记录的 OpenAI provider-specific state。ChatGPT/Codex 登录凭据不会被读取或修改。");
        }
        else
        {
            warnings.Add("尚无 OpenAI 历史状态；将使用保守最小配置，无法恢复未知的更早自定义值。");
        }

        roots["model"] = Quote(request.TargetModel);
        if (!settings.ProviderStates.ContainsKey(ProviderKind.OpenAI.ToString()))
        {
            roots["model_provider"] = Quote("openai");
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            roots["model_reasoning_effort"] = Quote(request.ReasoningEffort);
        }

        RemoveOrPreserveOfficialBearerTables(read, tables, removeTables, [], warnings, "切回 OpenAI 时");
    }

    /// <summary>组装切换到 DeepSeek 的配置：校验 catalog 与 CLI 版本、推理力度，并按 bearer 现状生成 Provider 表。</summary>
    private async Task ConfigureDeepSeekAsync(
        Dictionary<string, string?> roots,
        Dictionary<string, string?> tables,
        List<string> removeTables,
        ConfigReadResult read,
        AppSettings settings,
        SwitchRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DeepSeekCatalogPath) || !File.Exists(request.DeepSeekCatalogPath))
        {
            throw new InvalidOperationException("DeepSeek 官方 catalog 尚未准备好。");
        }

        byte[] catalogBytes = await File.ReadAllBytesAsync(request.DeepSeekCatalogPath, cancellationToken).ConfigureAwait(false);
        using JsonDocument catalog = DeepSeekCatalogService.ValidateCatalog(catalogBytes);
        JsonElement? selected = catalog.RootElement.GetProperty("models").EnumerateArray().FirstOrDefault(model => model.GetProperty("slug").GetString() == request.TargetModel);
        if (selected is null || selected.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("DeepSeek catalog 中不存在所选模型。");
        }

        string required = selected.Value.GetProperty("minimal_client_version").GetString()!;
        CodexEnvironmentInfo environment = await runtimeProbe.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!SemanticVersion.IsAtLeast(environment.CliVersion, required))
        {
            throw new InvalidOperationException($"当前 Codex 版本过低，{request.TargetModel} metadata 要求至少 {required}。");
        }

        roots["model"] = Quote(request.TargetModel);
        roots["model_provider"] = Quote("deepseek");
        roots["model_catalog_json"] = Quote(Path.GetFullPath(request.DeepSeekCatalogPath));
        roots["forced_login_method"] = Quote("api");
        roots["preferred_auth_method"] = null;
        roots["model_context_window"] = null;
        roots["model_auto_compact_token_limit"] = null;
        roots["tool_output_token_limit"] = GetSavedProviderRoot(settings, ProviderKind.DeepSeek, "tool_output_token_limit");
        string effort = request.ReasoningEffort ?? GetDefaultReasoning(selected.Value) ?? "high";
        HashSet<string> allowed = GetReasoningLevels(selected.Value);
        if (!allowed.Contains(effort))
        {
            throw new InvalidOperationException($"DeepSeek catalog 不支持 reasoning effort: {effort}");
        }

        roots["model_reasoning_effort"] = Quote(effort);

        string? existing = ComposeTableTree(read, "model_providers.deepseek");
        if (existing is not null && existing.Contains("experimental_bearer_token", StringComparison.Ordinal))
        {
            // 该表归官方脚本所有：保留其原始字节/注释/顺序，
            // 而不是删除后重建一个携带 token 的表。
            removeTables.RemoveAll(table => table == "model_providers.deepseek");
            warnings.Add("检测到 DeepSeek 官方明文 bearer 配置：本次继续兼容，不迁移、不复制、不显示 Token。");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.CredentialHelperPath) || !File.Exists(request.CredentialHelperPath))
            {
                throw new InvalidOperationException("Credential Helper 尚未安装到稳定路径。");
            }

            if (!secretStore.Exists(CredentialNames.DeepSeek))
            {
                throw new InvalidOperationException("尚未在 Windows Credential Manager 配置 DeepSeek Token。");
            }

            tables["model_providers.deepseek"] = BuildCommandProviderBody("model_providers.deepseek", "deepseek", "https://api.deepseek.com/", request.CredentialHelperPath, CredentialNames.DeepSeek);
            removeTables.RemoveAll(table => table == "model_providers.deepseek");
        }

        RemoveOrPreserveOfficialBearerTables(read, tables, removeTables, ["model_providers.deepseek"], warnings, "切换到 DeepSeek 时");
    }

    /// <summary>组装切换到 GLM 的配置：校验 catalog（版本门槛仅在声明时生效）、推理力度，并按 bearer 现状生成 Provider 表。</summary>
    private async Task ConfigureGlmAsync(
        Dictionary<string, string?> roots,
        Dictionary<string, string?> tables,
        List<string> removeTables,
        ConfigReadResult read,
        AppSettings settings,
        SwitchRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.GlmCatalogPath) || !File.Exists(request.GlmCatalogPath))
        {
            throw new InvalidOperationException("GLM 官方 catalog 尚未准备好。");
        }

        byte[] catalogBytes = await File.ReadAllBytesAsync(request.GlmCatalogPath, cancellationToken).ConfigureAwait(false);
        using JsonDocument catalog = GlmCatalogService.ValidateCatalog(catalogBytes);
        JsonElement? selected = catalog.RootElement.GetProperty("models").EnumerateArray().FirstOrDefault(model => model.GetProperty("slug").GetString() == request.TargetModel);
        if (selected is null || selected.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("GLM catalog 中不存在所选模型。");
        }

        // 官方 GLM models.json 不带 minimal_client_version；仅当模型声明了该字段时才做版本门槛
        if (selected.Value.TryGetProperty("minimal_client_version", out JsonElement minimum) &&
            minimum.ValueKind == JsonValueKind.String && minimum.GetString() is string required && !string.IsNullOrWhiteSpace(required))
        {
            CodexEnvironmentInfo environment = await runtimeProbe.DetectAsync(cancellationToken).ConfigureAwait(false);
            if (!SemanticVersion.IsAtLeast(environment.CliVersion, required))
            {
                throw new InvalidOperationException($"当前 Codex 版本过低，{request.TargetModel} metadata 要求至少 {required}。");
            }
        }

        GlmPlatform platform = request.GlmPlatform ?? throw new InvalidOperationException("GLM 平台未选择；请先选择智谱国内或国际 Z.ai。");
        roots["model"] = Quote(request.TargetModel);
        roots["model_provider"] = Quote(GlmPlatforms.ProviderId);
        roots["model_catalog_json"] = Quote(Path.GetFullPath(request.GlmCatalogPath));
        roots["forced_login_method"] = null;
        roots["preferred_auth_method"] = null;
        roots["model_context_window"] = null;
        roots["model_auto_compact_token_limit"] = null;
        roots["tool_output_token_limit"] = GetSavedProviderRoot(settings, ProviderKind.GLM, "tool_output_token_limit");
        string? effort = request.ReasoningEffort ?? GetDefaultReasoning(selected.Value);
        HashSet<string> allowed = GetReasoningLevels(selected.Value);
        if (effort is not null && allowed.Count > 0 && !allowed.Contains(effort))
        {
            throw new InvalidOperationException($"GLM catalog 不支持 reasoning effort: {effort}");
        }

        roots["model_reasoning_effort"] = effort is null ? null : Quote(effort);

        if (HasExperimentalBearerTable(read, GlmPlatforms.ProviderTableName))
        {
            string? existingBaseUrl = GetTableStringValue(read, GlmPlatforms.ProviderTableName, "base_url");
            if (!AreEquivalentProviderUrls(existingBaseUrl, GlmPlatforms.BaseUrl(platform)))
            {
                throw new InvalidOperationException("GLM 官方 bearer provider table 的 base_url 与当前选择的平台不一致；为避免把请求静默发送到错误平台，请先在官方配置中切换 endpoint 或删除该表后重新预览。");
            }

            // 该表归官方 GLM 指南/助手所有：保留其原始字节/注释/顺序，
            // 而不是删除后重建一个携带 token 的表。
            removeTables.RemoveAll(table => table == GlmPlatforms.ProviderTableName);
            warnings.Add("检测到 GLM 官方明文 bearer 配置：本次继续兼容，不迁移、不复制、不显示 Token。");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.CredentialHelperPath) || !File.Exists(request.CredentialHelperPath))
            {
                throw new InvalidOperationException("Credential Helper 尚未安装到稳定路径。");
            }

            if (!secretStore.Exists(CredentialNames.Glm))
            {
                throw new InvalidOperationException("尚未在 Windows Credential Manager 配置 GLM Token。");
            }

            tables[GlmPlatforms.ProviderTableName] = BuildCommandProviderBody(GlmPlatforms.ProviderTableName, GlmPlatforms.ProviderId, GlmPlatforms.BaseUrl(platform), request.CredentialHelperPath, CredentialNames.Glm);
            removeTables.RemoveAll(table => table == GlmPlatforms.ProviderTableName);
        }

        RemoveOrPreserveOfficialBearerTables(read, tables, removeTables, [GlmPlatforms.ProviderTableName], warnings, "切换到 GLM 时");
        warnings.Add("GLM 能力以官方 catalog metadata 为准；Plan/Goal/MCP 等未实测能力保持 Untested。");
    }

    /// <summary>组装切换到 LM Studio 的配置：强校验上下文/压缩/工具输出与推理力度，生成（或不生成）本地 Provider 表。</summary>
    private void ConfigureLmStudio(Dictionary<string, string?> roots, Dictionary<string, string?> tables, List<string> removeTables, ConfigReadResult read, SwitchRequest request, List<string> warnings)
    {
        if (request.TargetModelType is not null && !request.TargetModelType.Equals("llm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"所选 LM Studio instance 类型为 {request.TargetModelType}，禁止将非 LLM 模型配置给 Codex。");
        }

        if (request.ContextWindow is null or < 2_048)
        {
            throw new InvalidOperationException("LM Studio 未返回有效的实际 loaded context；禁止安全切换。");
        }

        int context = request.ContextWindow.Value;
        int suggestedCompact = SuggestAutoCompact(context);
        int compact = request.AutoCompactTokenLimit ?? throw new InvalidOperationException("LM Studio 请求未完成 Auto Compact 标准化。");
        if (compact <= 0 || compact >= context || context - compact < 1024)
        {
            throw new InvalidOperationException("Auto Compact 必须小于实际 context，并保留安全余量。");
        }

        if (request.ToolOutputTokenLimit is not int toolOutput || toolOutput <= 0 || toolOutput >= compact)
        {
            throw new InvalidOperationException("Tool Output Limit 必须为正数且小于 Auto Compact。");
        }

        if (compact > suggestedCompact)
        {
            warnings.Add($"手动 Auto Compact {compact:N0} 高于平衡策略建议值 {suggestedCompact:N0}；本次仍允许切换，但仅剩 {context - compact:N0} tokens 硬窗口余量。");
        }

        // Codex 源码 merge_configured_model_providers 对非 Bedrock 内置 ID（openai/ollama/lmstudio）
        // 一律 or_insert：用户定义的 [model_providers.lmstudio] 会被静默忽略，内置定义永远获胜，
        // 因此流韧性键只能写入自建表，LM Studio 一律使用 lmstudio_local_cmm。
        string providerId = request.LmStudioProviderId ?? "lmstudio_local_cmm";
        if (providerId == "lmstudio")
        {
            throw new InvalidOperationException("Codex 内置 lmstudio Provider 无法被 config.toml 覆盖，流韧性键不会生效；LM Studio 必须使用 lmstudio_local_cmm。");
        }

        if (providerId != "lmstudio_local_cmm")
        {
            throw new InvalidOperationException("LM Studio provider ID 不受支持。");
        }

        if (request.LmStudioEndpoint is null)
        {
            throw new InvalidOperationException("LM Studio endpoint 缺失。");
        }

        LmStudioEndpointPolicy.Validate(request.LmStudioEndpoint);
        IReadOnlySet<string> allowedReasoningEfforts = ReasoningEffortPolicy.ParseAllowed(request.TargetAllowedCodexReasoningEfforts);
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort) && !allowedReasoningEfforts.Contains(request.ReasoningEffort))
        {
            throw new InvalidOperationException($"LM Studio 未报告与 Codex 精确匹配的 reasoning effort: {request.ReasoningEffort}；只有 on/off 或 capability 未知时必须选择不写入。");
        }

        roots["model"] = Quote(request.TargetModel);
        roots["model_provider"] = Quote(providerId);
        roots["model_context_window"] = context.ToString(CultureInfo.InvariantCulture);
        roots["model_auto_compact_token_limit"] = compact.ToString(CultureInfo.InvariantCulture);
        roots["model_auto_compact_token_limit_scope"] = Quote("total");
        roots["tool_output_token_limit"] = toolOutput.ToString(CultureInfo.InvariantCulture);
        roots["model_catalog_json"] = null;
        roots["model_reasoning_effort"] = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : Quote(request.ReasoningEffort);
        roots["forced_login_method"] = null;
        roots["preferred_auth_method"] = null;
        roots["openai_base_url"] = null;
        string lmStudioTablePath = "model_providers." + providerId;
        RemoveOrPreserveOfficialBearerTables(read, tables, removeTables, [lmStudioTablePath], warnings, "切换到 LM Studio 时");

        // 流韧性键只能放在 model_providers 表内才会生效，否则长 prefill 会被默认 5 分钟空闲超时判死并重试。
        Uri endpoint = request.LmStudioEndpoint.AbsoluteUri.EndsWith('/') ? request.LmStudioEndpoint : new Uri(request.LmStudioEndpoint.AbsoluteUri + "/");
        string baseUrl = new Uri(endpoint, "v1").AbsoluteUri.TrimEnd('/');
        if (request.LmStudioRequiresAuthentication)
        {
            if (string.IsNullOrWhiteSpace(request.CredentialHelperPath) || !File.Exists(request.CredentialHelperPath))
            {
                throw new InvalidOperationException("Credential Helper 尚未安装。");
            }

            if (!secretStore.Exists(CredentialNames.LmStudio))
            {
                throw new InvalidOperationException("LM Studio 返回 401，但尚未保存 Token。");
            }

            tables[lmStudioTablePath] = BuildCommandProviderBody(lmStudioTablePath, "LM Studio Local", baseUrl, request.CredentialHelperPath, CredentialNames.LmStudio, LmStudioStreamResilience.TableBody);
        }
        else
        {
            tables[lmStudioTablePath] = $"name = {Quote("LM Studio Local")}\nbase_url = {Quote(baseUrl)}\nwire_api = \"responses\"\n" + LmStudioStreamResilience.TableBody;
        }

        removeTables.RemoveAll(table => table == lmStudioTablePath);

        warnings.Add("本地模型未生成或复制 GPT/DeepSeek metadata；未被实测的 Plan/Goal/MCP 等能力保持 Untested。Auto Compact 为管理器安全建议值。");
        if (request.TargetSupportsToolUse == false)
        {
            warnings.Add("所选模型由 LM Studio 声明为未针对 Tool Use 训练，Codex Agent 能力很可能受限。");
        }
        else if (request.TargetSupportsToolUse is null)
        {
            warnings.Add("fallback Models API 未提供 Tool Use 能力，状态保持 Unknown；建议先运行 Level 2。");
        }

        if (request.TargetSupportsReasoning is null)
        {
            warnings.Add("未发现可依据的 reasoning capability；未写入 reasoning effort。");
        }
        else if (request.TargetSupportsReasoning == true && allowedReasoningEfforts.Count == 0)
        {
            warnings.Add("LM Studio 仅报告 on/off reasoning capability，未猜测为 Codex effort；model_reasoning_effort 将不写入。");
        }
    }

    /// <summary>判断指定表（含子表）原文中是否含 experimental_bearer_token。</summary>
    private static bool HasExperimentalBearerTable(ConfigReadResult read, string tablePath) =>
        ComposeTableTree(read, tablePath)?.Contains("experimental_bearer_token", StringComparison.Ordinal) == true;

    /// <summary>读取指定 Provider 父表中的简单字符串键（用于验证官方 bearer 表的平台 endpoint）。</summary>
    private static string? GetTableStringValue(ConfigReadResult read, string tablePath, string key)
    {
        if (!read.TableBodies.TryGetValue(tablePath, out string? body))
        {
            return null;
        }

        try
        {
            TomlSourceDocument document = TomlSourceDocument.Parse($"[{tablePath}]\n{body}");
            TomlSourceAssignment? assignment = document.Assignments.SingleOrDefault(item =>
                item.Segments.Count == tablePath.Split('.').Length + 1 &&
                item.Segments[^1].Equals(key, StringComparison.Ordinal));
            return assignment?.StringValue ?? CodexRuntimeProbe.Unquote(assignment?.RawValue);
        }
        catch (InvalidDataException)
        {
            // 官方表体若已不再是可解析 TOML，继续 fail closed，不能猜测 endpoint。
            return null;
        }
    }

    /// <summary>比较 Provider endpoint，允许官方配置尾随斜杠但不允许跨平台或其他 authority。</summary>
    private static bool AreEquivalentProviderUrls(string? actual, string expected)
    {
        if (!Uri.TryCreate(actual, UriKind.Absolute, out Uri? actualUri) || !Uri.TryCreate(expected, UriKind.Absolute, out Uri? expectedUri))
        {
            return false;
        }

        return actualUri.Scheme.Equals(expectedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            actualUri.Host.Equals(expectedUri.Host, StringComparison.OrdinalIgnoreCase) &&
            actualUri.Port == expectedUri.Port &&
            actualUri.AbsolutePath.TrimEnd('/').Equals(expectedUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    /// <summary>
    /// 清理纳管 Provider 表，但保留调用方刚配置的表：
    /// 携带官方明文 bearer token 的表保持原始文本（休眠）——
    /// 当前路由始终由 model_provider 决定，与这些表无关。
    /// </summary>
    private static void RemoveOrPreserveOfficialBearerTables(ConfigReadResult read, Dictionary<string, string?> tables, List<string> removeTables, IReadOnlyCollection<string> activeTables, List<string> warnings, string routeDescription)
    {
        foreach (string table in ManagedConfigKeys.ProviderTables)
        {
            if (activeTables.Contains(table))
            {
                continue;
            }

            if (HasExperimentalBearerTable(read, table))
            {
                string owner = table == GlmPlatforms.ProviderTableName ? "GLM 官方指南" : "DeepSeek 官方脚本";
                removeTables.RemoveAll(item => item == table);
                warnings.Add($"检测到 {owner}拥有的明文 bearer provider table（{table}）；{routeDescription}保留其原始文本，当前请求仍由激活的 model_provider 路由。");
            }
            else
            {
                tables[table] = null;
            }
        }
    }

    /// <summary>LM Studio 目标时执行指令层级前置探测，不兼容即抛 LmStudioCompatibilityException；其余 Provider 返回 null。</summary>
    private async Task<CodexInstructionHierarchyProbeResult?> EnsureLmStudioPreflightAsync(SwitchRequest request, CancellationToken cancellationToken)
    {
        if (request.TargetProvider != ProviderKind.LmStudio)
        {
            return null;
        }

        CodexInstructionHierarchyProbeResult result = await lmStudioPreflight.ProbeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsCompatible)
        {
            throw new LmStudioCompatibilityException(result);
        }

        return result;
    }

    /// <summary>
    /// 规范化切换请求：推理力度清洗为小写；LM Studio 目标时按 Automatic/Manual 模式
    /// 补全并校验自动压缩阈值与工具输出上限，产出可执行的标准请求。
    /// </summary>
    internal static SwitchRequest NormalizeSwitchRequest(SwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetModel);
        string? reasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : request.ReasoningEffort.Trim().ToLowerInvariant();
        if (request.TargetProvider != ProviderKind.LmStudio)
        {
            return request with { ReasoningEffort = reasoningEffort };
        }

        if (request.ContextWindow is not int context || context < 2_048)
        {
            throw new InvalidOperationException("LM Studio 未返回有效的实际 loaded context；禁止安全切换。");
        }

        int suggestedCompact = SuggestAutoCompact(context);
        int compact;
        AutoCompactMode compactMode;
        switch (request.AutoCompactMode)
        {
            case null:
                // 未指定模式：带自定义阈值且不等于建议值视为 Manual，否则 Automatic
                compact = request.AutoCompactTokenLimit ?? suggestedCompact;
                compactMode = request.AutoCompactTokenLimit is null || compact == suggestedCompact ? AutoCompactMode.Automatic : AutoCompactMode.Manual;
                break;
            case AutoCompactMode.Automatic:
                if (request.AutoCompactTokenLimit is int automaticLimit && automaticLimit != suggestedCompact)
                {
                    throw new InvalidOperationException($"Automatic Auto Compact 只能为空或等于当前建议值 {suggestedCompact:N0}。");
                }

                compact = suggestedCompact;
                compactMode = AutoCompactMode.Automatic;
                break;
            case AutoCompactMode.Manual:
                compact = request.AutoCompactTokenLimit ?? throw new InvalidOperationException("Manual Auto Compact 必须提供明确的 token limit。");
                compactMode = AutoCompactMode.Manual;
                break;
            default:
                throw new InvalidOperationException("Auto Compact 模式无效。");
        }

        if (compact <= 0 || compact >= context || context - compact < 1_024)
        {
            throw new InvalidOperationException("Auto Compact 必须小于实际 context，并保留至少 1,024 tokens 安全余量。");
        }

        int toolOutput = request.ToolOutputTokenLimit ?? SuggestToolOutputLimit(context);
        if (toolOutput <= 0 || toolOutput >= compact)
        {
            throw new InvalidOperationException("Tool Output Limit 必须为正数且小于 Auto Compact。");
        }

        return request with
        {
            ReasoningEffort = reasoningEffort,
            AutoCompactTokenLimit = compact,
            AutoCompactMode = compactMode,
            ToolOutputTokenLimit = toolOutput,
        };
    }

    /// <summary>按策略计算 Secondary Override 替换表：FollowMain 替换选中项，RestoreOriginal 还原记录的原始值。</summary>
    private static Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> BuildSecondaryReplacements(SwitchRequest request, IReadOnlyList<SecondaryModelOverride> overrides, AppSettings settings, List<string> warnings)
    {
        Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> result = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? selected = ParseOverrideSelection(request.SecondaryOverrideSelectionJson);
        if (selected is not null)
        {
            // 显式选择必须对应扫描结果里真实存在的键，否则视为扫描后配置已变化
            HashSet<string> known = overrides.Select(OverrideStateKey).ToHashSet(SecondaryOverrideKeyComparer.Instance);
            string[] unknown = selected.Where(item => !known.Contains(item)).ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException("Secondary Override 选择在扫描后已失效，请重新加载。");
            }
        }

        if (request.SecondaryOverridePolicy == SecondaryOverridePolicy.FollowMain)
        {
            foreach (SecondaryModelOverride item in overrides.Where(item => IsOverrideSelected(item, selected)))
            {
                AddReplacement(result, item, new SecondaryOverrideReplacement(request.TargetModel));
            }

            int unselectedExternal = overrides.Count(item => !item.CanEdit && !IsOverrideSelected(item, selected));
            if (unselectedExternal > 0)
            {
                warnings.Add($"有 {unselectedExternal} 个外部 profile/agent/project override 未显式勾选，保持不变。");
            }
        }
        else if (request.SecondaryOverridePolicy == SecondaryOverridePolicy.RestoreOriginal)
        {
            foreach (SecondaryModelOverride item in overrides.Where(item => IsOverrideSelectedForRestore(item, selected, settings)))
            {
                if (settings.SecondaryOverrideOriginals.TryGetValue(OverrideStateKey(item), out string? original))
                {
                    AddReplacement(result, item, DecodeSecondaryOriginal(original));
                }
            }
        }

        return result;
    }

    /// <summary>登记一条替换；RawTomlValue 缺失的条目无法安全编辑，直接抛错。</summary>
    private static void AddReplacement(Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> replacements, SecondaryModelOverride item, SecondaryOverrideReplacement replacement)
    {
        if (item.RawTomlValue is null)
        {
            throw new InvalidOperationException($"Secondary Override 无法安全编辑，请先修复或重新扫描: {item.FilePath} :: {item.KeyPath}");
        }

        string file = Path.GetFullPath(item.FilePath);
        if (!replacements.TryGetValue(file, out Dictionary<string, SecondaryOverrideReplacement>? values))
        {
            values = new Dictionary<string, SecondaryOverrideReplacement>(StringComparer.Ordinal);
            replacements[file] = values;
        }

        values[item.KeyPath] = replacement;
    }

    /// <summary>把 override 原始值（模型 ID + 原始 TOML 值）序列化为存储格式。</summary>
    private static string EncodeSecondaryOriginal(SecondaryModelOverride item) => JsonSerializer.Serialize(new SecondaryOriginalValue(item.Model, item.RawTomlValue));

    /// <summary>还原存储的原始值；解析失败时按 v1 旧格式（直接存模型 ID）处理。</summary>
    private static SecondaryOverrideReplacement DecodeSecondaryOriginal(string stored)
    {
        try
        {
            SecondaryOriginalValue? value = JsonSerializer.Deserialize<SecondaryOriginalValue>(stored);
            if (value is not null && !string.IsNullOrEmpty(value.Value))
            {
                return new SecondaryOverrideReplacement(value.Value, value.RawTomlValue);
            }
        }
        catch (JsonException)
        {
            // v1 版本直接存储语义模型 ID，按旧格式兜底
        }

        return new SecondaryOverrideReplacement(stored);
    }

    /// <summary>解析 UI 提交的 override 显式选择 JSON（null 表示未显式勾选）。</summary>
    private static HashSet<string>? ParseOverrideSelection(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            List<SecondaryOverrideTarget> targets = JsonSerializer.Deserialize<List<SecondaryOverrideTarget>>(json) ?? [];
            return targets.Select(target => OverrideStateKey(target.FilePath, target.KeyPath)).ToHashSet(SecondaryOverrideKeyComparer.Instance);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Secondary Override 选择数据无效。", exception);
        }
    }

    /// <summary>判断 override 是否被选中：未显式选择时默认“可编辑项全部选中”。</summary>
    private static bool IsOverrideSelected(SecondaryModelOverride item, HashSet<string>? selected) =>
        selected is null ? item.CanEdit : selected.Contains(OverrideStateKey(item));

    /// <summary>判断 override 是否入选“还原原始值”：已有记录且（未显式选择或被选中）。</summary>
    private static bool IsOverrideSelectedForRestore(SecondaryModelOverride item, HashSet<string>? selected, AppSettings settings) =>
        settings.SecondaryOverrideOriginals.ContainsKey(OverrideStateKey(item)) && (selected is null || selected.Contains(OverrideStateKey(item)));

    /// <summary>判断 override 在计划中是否真的产生了变更（可编辑项按键路径，外部项按“文件::键路径”后缀）。</summary>
    private static bool WasOverrideMutated(SwitchPlan plan, SecondaryModelOverride item)
    {
        string externalSuffix = "::" + item.KeyPath;
        string filePath = Path.GetFullPath(item.FilePath);
        return plan.Mutations.Any(mutation => item.CanEdit
            ? mutation.KeyPath.Equals(item.KeyPath, StringComparison.Ordinal)
            : mutation.KeyPath.EndsWith(externalSuffix, StringComparison.Ordinal) &&
              mutation.KeyPath[..^externalSuffix.Length].Equals(filePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>候选语义校验：model、model_provider 必须与目标一致；LM Studio 另校验上下文/压缩/工具输出，云 Provider 另校验 catalog 路径。</summary>
    private static void ValidateCandidateSemantics(ConfigReadResult read, SwitchRequest request)
    {
        string? actualModel = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model"));
        if (!string.Equals(actualModel, request.TargetModel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("候选配置语义检查失败：model 与目标不一致。");
        }

        string actualProvider = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model_provider")) ?? "openai";
        string expectedProvider = request.TargetProvider switch
        {
            ProviderKind.OpenAI => "openai",
            ProviderKind.DeepSeek => "deepseek",
            ProviderKind.LmStudio => request.LmStudioProviderId ?? "lmstudio_local_cmm",
            ProviderKind.GLM => GlmPlatforms.ProviderId,
            _ => throw new InvalidOperationException("unknown provider"),
        };
        if (!string.Equals(actualProvider, expectedProvider, StringComparison.Ordinal))
        {
            throw new InvalidDataException("候选配置语义检查失败：model_provider 与目标不一致。");
        }

        if (request.TargetProvider == ProviderKind.LmStudio)
        {
            string? compactScope = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model_auto_compact_token_limit_scope"));
            if (!int.TryParse(read.RootValues.GetValueOrDefault("model_context_window"), CultureInfo.InvariantCulture, out int context) || context != request.ContextWindow ||
                !int.TryParse(read.RootValues.GetValueOrDefault("model_auto_compact_token_limit"), CultureInfo.InvariantCulture, out int compact) || compact != request.AutoCompactTokenLimit ||
                !int.TryParse(read.RootValues.GetValueOrDefault("tool_output_token_limit"), CultureInfo.InvariantCulture, out int toolOutput) || toolOutput != request.ToolOutputTokenLimit ||
                !string.Equals(compactScope, "total", StringComparison.Ordinal))
            {
                throw new InvalidDataException("候选配置语义检查失败：Local context/compaction/tool output 不一致。");
            }
        }

        if (request.TargetProvider is ProviderKind.DeepSeek or ProviderKind.GLM)
        {
            string? expectedCatalogPath = request.TargetProvider == ProviderKind.DeepSeek ? request.DeepSeekCatalogPath : request.GlmCatalogPath;
            string? catalog = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model_catalog_json"));
            if (string.IsNullOrWhiteSpace(catalog) || !Path.GetFullPath(catalog).Equals(Path.GetFullPath(expectedCatalogPath!), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"候选配置语义检查失败：{request.TargetProvider} catalog 路径不一致。");
            }
        }
    }

    /// <summary>读取设置里保存的某个 Provider 根键值；无记录返回 null。</summary>
    private static string? GetSavedProviderRoot(AppSettings settings, ProviderKind provider, string key) =>
        settings.ProviderStates.TryGetValue(provider.ToString(), out ProviderState? state) ? state.RootValues.GetValueOrDefault(key) : null;

    /// <summary>
    /// 捕获 Provider 状态快照：仅保存恢复所需的非敏感根键值；
    /// Provider 表可能含官方明文 token，刻意永不复制进 appsettings。
    /// openai_base_url 带疑似凭据的查询参数时直接中止切换。
    /// </summary>
    private static ProviderState CaptureProviderState(ProviderKind provider, ConfigReadResult read, string sha)
    {
        if (provider is not (ProviderKind.OpenAI or ProviderKind.DeepSeek or ProviderKind.GLM))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "只允许持久化 OpenAI、DeepSeek 或 GLM provider state。");
        }

        Dictionary<string, string?> roots = ManagedConfigKeys.Root.ToDictionary(key => key, key => read.RootValues.TryGetValue(key, out string? value) ? value : null, StringComparer.Ordinal);
        if (ContainsSensitiveUrlQuery(roots.GetValueOrDefault("openai_base_url")))
        {
            throw new InvalidOperationException("openai_base_url 含疑似凭据查询参数；为避免把 Secret 写入 appsettings，已中止切换。请改用受支持的凭据机制后重试。");
        }

        // 表体统一置 null：不把可能含官方明文 bearer token 的表复制进 appsettings
        Dictionary<string, string?> tables = ManagedConfigKeys.ProviderTables.ToDictionary(key => key, _ => (string?)null, StringComparer.Ordinal);
        return new ProviderState(provider, DateTimeOffset.Now, roots, tables, sha);
    }

    /// <summary>判断 URL（可为 TOML 引号串）的查询参数里是否含疑似凭据的参数名。</summary>
    internal static bool ContainsSensitiveUrlQuery(string? rawValue)
    {
        string? value = CodexRuntimeProbe.Unquote(rawValue);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Any(IsSensitiveQueryParameterName);
    }

    /// <summary>判断单个查询参数名是否敏感：解码后按分隔符切词比对敏感词表，拼接后也比对一次。</summary>
    private static bool IsSensitiveQueryParameterName(string encodedName)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(encodedName.Replace('+', ' ')).ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            return true;
        }

        string[] tokens = decoded.Split(decoded.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HashSet<string> sensitiveTokens = new(StringComparer.Ordinal)
        {
            "key", "token", "secret", "password", "credential", "signature",
        };
        if (tokens.Any(sensitiveTokens.Contains))
        {
            return true;
        }

        // 去掉全部非字母数字后整体比对，覆盖 apiKey / access_token 这类拼接写法
        string compact = string.Concat(decoded.Where(char.IsLetterOrDigit));
        return compact is "key" or "apikey" or "accesstoken" or "token" or "secret" or "clientsecret" or "password" or "credential" or "signature";
    }

    /// <summary>把某表及其全部子表的原文拼成一段文本（父表在前，子表按层级与字典序）。</summary>
    private static string? ComposeTableTree(ConfigReadResult read, string parent)
    {
        List<string> parts = [];
        foreach ((string path, string body) in read.TableBodies.Where(pair => pair.Key == parent || pair.Key.StartsWith(parent + ".", StringComparison.Ordinal)).OrderBy(pair => pair.Key.Count(character => character == '.')).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (path == parent)
            {
                parts.Add(body.TrimEnd('\r', '\n'));
            }
            else
            {
                parts.Add($"[{path}]\n{body.TrimEnd('\r', '\n')}");
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>生成“凭据命令”式 Provider 表体：auth 子表调用凭据助手按名取 token；extraBodyLines 插在主表键与 auth 子表之间。</summary>
    private static string BuildCommandProviderBody(string tablePath, string name, string baseUrl, string helperPath, string credentialName, string? extraBodyLines = null)
    {
        string body = $"name = {Quote(name)}\nbase_url = {Quote(baseUrl)}\nwire_api = \"responses\"";
        if (!string.IsNullOrEmpty(extraBodyLines))
        {
            body += "\n" + extraBodyLines;
        }

        return body + $"\n\n[{tablePath}.auth]\ncommand = {Quote(Path.GetFullPath(helperPath))}\nargs = [{Quote(credentialName)}]\ntimeout_ms = 5000\nrefresh_interval_ms = 0";
    }

    /// <summary>读取模型 metadata 里的 supported_reasoning_levels 集合；缺失返回空集。</summary>
    private static HashSet<string> GetReasoningLevels(JsonElement model)
    {
        if (!model.TryGetProperty("supported_reasoning_levels", out JsonElement levels))
        {
            return [];
        }

        return levels.EnumerateArray().Select(item => item.GetProperty("effort").GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>读取模型 metadata 的 default_reasoning_level；缺失返回 null。</summary>
    private static string? GetDefaultReasoning(JsonElement model) => model.TryGetProperty("default_reasoning_level", out JsonElement level) ? level.GetString() : null;

    /// <summary>JSON 序列化为 TOML 字符串字面量（含引号与转义）。</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value);

    /// <summary>严格 UTF-8 解码（自动剥离 BOM）。</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;
        return new UTF8Encoding(false, true).GetString(data);
    }

    /// <summary>override 状态键：“文件全路径|键路径”。</summary>
    private static string OverrideStateKey(SecondaryModelOverride item) => Path.GetFullPath(item.FilePath) + "|" + item.KeyPath;

    /// <summary>override 状态键（文件路径与键路径分开传入的形态）。</summary>
    private static string OverrideStateKey(string filePath, string keyPath) => Path.GetFullPath(filePath) + "|" + keyPath;

    /// <summary>计算计划哈希：请求序列化 + 每个文件（按路径序）的路径、期望指纹与候选内容哈希。</summary>
    private static string ComputePlanHash(SwitchRequest request, IReadOnlyList<PlannedFileChange> changes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(request));
        foreach (PlannedFileChange change in changes.OrderBy(change => Path.GetFullPath(change.Path), StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFullPath(change.Path)));
            hash.AppendData(Encoding.ASCII.GetBytes(change.ExpectedFingerprint.Sha256));
            hash.AppendData(SHA256.HashData(change.CandidateBytes ?? []));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>override 原始值的存储形态：语义值 + 原始 TOML 值。</summary>
    private sealed record SecondaryOriginalValue(string Value, string? RawTomlValue);
}
