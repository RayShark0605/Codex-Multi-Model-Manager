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

    public const int AutoCompactPolicyVersion = 2;

    public static int SuggestAutoCompact(int contextWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextWindow, 2_048);
        const int absoluteReserve = 24_576;
        const double maximumUsageRatio = 0.80;
        int proportionalLimit = (int)Math.Floor(contextWindow * maximumUsageRatio);
        int boundedAbsoluteReserve = Math.Min(absoluteReserve, contextWindow / 2);
        return Math.Min(proportionalLimit, contextWindow - boundedAbsoluteReserve);
    }

    public static int SuggestToolOutputLimit(int contextWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextWindow, 2_048);
        int adaptiveLimit = Math.Clamp(contextWindow / 50, 2_048, 4_096);
        int compactLimit = SuggestAutoCompact(contextWindow);
        int smallContextLimit = Math.Max(256, compactLimit / 4);
        return Math.Min(adaptiveLimit, smallContextLimit);
    }

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
    internal static int SuggestLegacyAutoCompact(int contextWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextWindow, 2_048);
        return Math.Min((int)Math.Floor(contextWindow * 0.90), contextWindow - 8_192);
    }

    public async Task<SwitchPlan> CreatePlanAsync(SwitchRequest request, CancellationToken cancellationToken = default)
    {
        SwitchRequest effectiveRequest = NormalizeSwitchRequest(request);
        CodexInstructionHierarchyProbeResult? preflight = await EnsureLmStudioPreflightAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
        return await CreatePlanCoreAsync(effectiveRequest, preflight, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SwitchPlan> CreatePlanCoreAsync(
        SwitchRequest request,
        CodexInstructionHierarchyProbeResult? preflight,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetModel);
        string configPath = Path.Combine(homeProvider.GetCodexHome(), "config.toml");
        TextFileSnapshot source = await TextFileCodec.ReadAsync(configPath, cancellationToken).ConfigureAwait(false);
        patchEngine.Validate(source.Text);
        ConfigReadResult read = patchEngine.Read(source.Text);
        ProviderKind sourceProvider = CodexRuntimeProbe.ParseProvider(CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model_provider")) ?? "openai");
        string? sourceModel = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model"));
        AppSettings settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
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
            case ProviderKind.LmStudio:
                ConfigureLmStudio(roots, tables, removeTables, read, request, warnings);
                break;
            default:
                throw new InvalidOperationException("unknown provider：已拒绝生成切换计划。");
        }

        ConfigPatchResult patch = patchEngine.Apply(source.Text, new ConfigPatchRequest(roots, tables, removeTables));
        string candidateText = patch.Text;
        List<ConfigMutation> allMutations = [.. patch.Mutations];
        Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> secondaryReplacements = BuildSecondaryReplacements(request, overrides, settings, warnings);
        secondaryReplacements.TryGetValue(Path.GetFullPath(configPath), out Dictionary<string, SecondaryOverrideReplacement>? primaryReplacements);
        (candidateText, IReadOnlyList<ConfigMutation> secondaryMutations) = SecondaryOverridePatcher.Apply(candidateText, primaryReplacements ?? new Dictionary<string, SecondaryOverrideReplacement>());
        allMutations.AddRange(secondaryMutations);
        patchEngine.Validate(candidateText);

        ConfigReadResult finalRead = patchEngine.Read(candidateText);
        ValidateCandidateSemantics(finalRead, request);
        if (finalRead.McpServerCount != read.McpServerCount || finalRead.ProjectCount != read.ProjectCount ||
            finalRead.HookSectionCount != read.HookSectionCount || finalRead.PluginSectionCount != read.PluginSectionCount)
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

        foreach ((string filePath, Dictionary<string, SecondaryOverrideReplacement> replacements) in secondaryReplacements.Where(pair => !pair.Key.Equals(Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextFileSnapshot external = await TextFileCodec.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (!external.Fingerprint.Exists) throw new IOException($"选中的 Secondary Override 配置已不存在: {filePath}");
            patchEngine.Validate(external.Text);
            (string externalCandidate, IReadOnlyList<ConfigMutation> externalMutations) = SecondaryOverridePatcher.Apply(external.Text, replacements);
            if (externalMutations.Count == 0) continue;
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

        return new SwitchPlan(
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
            preflight);
    }

    public async Task CommitAsync(SwitchPlan preview, CancellationToken cancellationToken = default)
    {
        CodexEnvironmentInfo environment = await runtimeProbe.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (environment.IsRunning)
        {
            throw new InvalidOperationException("检测到 Codex/ChatGPT Desktop 或 codex 子进程仍在运行。请完全关闭后重新检测。");
        }

        await VerifyPreviewFingerprintsAsync(preview.Files, cancellationToken).ConfigureAwait(false);
        SwitchRequest effectiveRequest = NormalizeSwitchRequest(preview.Request);
        CodexInstructionHierarchyProbeResult? preflight = await EnsureLmStudioPreflightAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
        SwitchPlan regenerated = await CreatePlanCoreAsync(effectiveRequest, preflight, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.PlanHash, regenerated.PlanHash, StringComparison.Ordinal))
        {
            throw new IOException("配置文件在预览后发生变化，请重新加载并再次预览。");
        }

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

        PlannedFileChange configChange = regenerated.Files.Single(file => Path.GetFullPath(file.Path).Equals(configPath, StringComparison.OrdinalIgnoreCase));
        TextFileSnapshot before = await TextFileCodec.ReadAsync(configChange.Path, cancellationToken).ConfigureAwait(false);
        ConfigReadResult beforeRead = patchEngine.Read(before.Text);
        AppSettings settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (regenerated.SourceProvider is ProviderKind.OpenAI or ProviderKind.DeepSeek)
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

        byte[] settingsBytes = AppSettingsRepository.Serialize(settings);
        string settingsPath = settingsRepository.SettingsPath;
        FileFingerprint settingsFingerprint = await FileFingerprintService.CaptureAsync(settingsPath, cancellationToken).ConfigureAwait(false);
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
        await writer.WriteAsync([.. regenerated.Files, settingsChange], cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyPreviewFingerprintsAsync(
        IReadOnlyList<PlannedFileChange> files,
        CancellationToken cancellationToken)
    {
        foreach (PlannedFileChange file in files)
        {
            FileFingerprint actual = await FileFingerprintService.CaptureAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!FileFingerprintService.Matches(file.ExpectedFingerprint, actual))
            {
                throw new IOException($"配置文件在预览后发生变化，请重新加载: {Path.GetFileName(file.Path)}");
            }
        }
    }

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
            foreach (string key in ManagedConfigKeys.Root) roots[key] = state.RootValues.GetValueOrDefault(key);
            warnings.Add("将恢复上次由管理器记录的 OpenAI provider-specific state。ChatGPT/Codex 登录凭据不会被读取或修改。");
        }
        else
        {
            warnings.Add("尚无 OpenAI 历史状态；将使用保守最小配置，无法恢复未知的更早自定义值。");
        }

        roots["model"] = Quote(request.TargetModel);
        if (!settings.ProviderStates.ContainsKey(ProviderKind.OpenAI.ToString())) roots["model_provider"] = Quote("openai");
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) roots["model_reasoning_effort"] = Quote(request.ReasoningEffort);
        string? deepSeekTree = ComposeTableTree(read, "model_providers.deepseek");
        bool preserveOfficialBearer = deepSeekTree?.Contains("experimental_bearer_token", StringComparison.Ordinal) == true;
        foreach (string table in ManagedConfigKeys.ProviderTables)
        {
            if (table == "model_providers.deepseek" && preserveOfficialBearer)
            {
                removeTables.RemoveAll(item => item == table);
                warnings.Add("检测到 DeepSeek 官方脚本拥有的明文 bearer provider table；切回 OpenAI 时保留其原始文本，当前请求仍由 model_provider=openai 路由。");
            }
            else
            {
                tables[table] = null;
            }
        }
    }

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
        if (!allowed.Contains(effort)) throw new InvalidOperationException($"DeepSeek catalog 不支持 reasoning effort: {effort}");
        roots["model_reasoning_effort"] = Quote(effort);

        string? existing = ComposeTableTree(read, "model_providers.deepseek");
        if (existing is not null && existing.Contains("experimental_bearer_token", StringComparison.Ordinal))
        {
            // The official script owns this table. Leave its exact bytes/comments/order in
            // place instead of removing and re-appending a token-bearing table.
            removeTables.RemoveAll(table => table == "model_providers.deepseek");
            warnings.Add("检测到 DeepSeek 官方明文 bearer 配置：本次继续兼容，不迁移、不复制、不显示 Token。");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.CredentialHelperPath) || !File.Exists(request.CredentialHelperPath))
            {
                throw new InvalidOperationException("Credential Helper 尚未安装到稳定路径。");
            }

            if (!secretStore.Exists(CredentialNames.DeepSeek)) throw new InvalidOperationException("尚未在 Windows Credential Manager 配置 DeepSeek Token。");
            tables["model_providers.deepseek"] = BuildCommandProviderBody("model_providers.deepseek", "deepseek", "https://api.deepseek.com/", request.CredentialHelperPath, CredentialNames.DeepSeek);
            removeTables.RemoveAll(table => table == "model_providers.deepseek");
        }

        foreach (string table in ManagedConfigKeys.ProviderTables.Where(table => table != "model_providers.deepseek")) tables[table] = null;
    }

    private void ConfigureLmStudio(
        Dictionary<string, string?> roots,
        Dictionary<string, string?> tables,
        List<string> removeTables,
        ConfigReadResult read,
        SwitchRequest request,
        List<string> warnings)
    {
        if (request.TargetModelType is not null && !request.TargetModelType.Equals("llm", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"所选 LM Studio instance 类型为 {request.TargetModelType}，禁止将非 LLM 模型配置给 Codex。");
        if (request.ContextWindow is null or < 2_048) throw new InvalidOperationException("LM Studio 未返回有效的实际 loaded context；禁止安全切换。");
        int context = request.ContextWindow.Value;
        int suggestedCompact = SuggestAutoCompact(context);
        int compact = request.AutoCompactTokenLimit ?? throw new InvalidOperationException("LM Studio 请求未完成 Auto Compact 标准化。");
        if (compact <= 0 || compact >= context || context - compact < 1024) throw new InvalidOperationException("Auto Compact 必须小于实际 context，并保留安全余量。");
        if (request.ToolOutputTokenLimit is not int toolOutput || toolOutput <= 0 || toolOutput >= compact)
        {
            throw new InvalidOperationException("Tool Output Limit 必须为正数且小于 Auto Compact。");
        }

        if (compact > suggestedCompact)
        {
            warnings.Add($"手动 Auto Compact {compact:N0} 高于平衡策略建议值 {suggestedCompact:N0}；本次仍允许切换，但仅剩 {context - compact:N0} tokens 硬窗口余量。");
        }
        string providerId = request.LmStudioProviderId ?? "lmstudio";
        if (providerId is not ("lmstudio" or "lmstudio_local_cmm")) throw new InvalidOperationException("LM Studio provider ID 不受支持。");
        if (request.LmStudioEndpoint is null) throw new InvalidOperationException("LM Studio endpoint 缺失。");
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
        bool preserveOfficialBearer = ComposeTableTree(read, "model_providers.deepseek")?.Contains("experimental_bearer_token", StringComparison.Ordinal) == true;
        foreach (string table in ManagedConfigKeys.ProviderTables)
        {
            if (table == "model_providers.deepseek" && preserveOfficialBearer)
            {
                removeTables.RemoveAll(item => item == table);
                warnings.Add("检测到 DeepSeek 官方脚本拥有的明文 bearer provider table；切换到 LM Studio 时保留其原始文本，当前请求仍由 LM Studio provider 路由。");
            }
            else
            {
                tables[table] = null;
            }
        }

        if (providerId != "lmstudio")
        {
            string tablePath = "model_providers." + providerId;
            Uri endpoint = request.LmStudioEndpoint.AbsoluteUri.EndsWith('/') ? request.LmStudioEndpoint : new Uri(request.LmStudioEndpoint.AbsoluteUri + "/");
            string baseUrl = new Uri(endpoint, "v1").AbsoluteUri.TrimEnd('/');
            if (request.LmStudioRequiresAuthentication)
            {
                if (string.IsNullOrWhiteSpace(request.CredentialHelperPath) || !File.Exists(request.CredentialHelperPath)) throw new InvalidOperationException("Credential Helper 尚未安装。");
                if (!secretStore.Exists(CredentialNames.LmStudio)) throw new InvalidOperationException("LM Studio 返回 401，但尚未保存 Token。");
                tables[tablePath] = BuildCommandProviderBody(tablePath, "LM Studio Local", baseUrl, request.CredentialHelperPath, CredentialNames.LmStudio);
            }
            else
            {
                tables[tablePath] = $"name = {Quote("LM Studio Local")}\nbase_url = {Quote(baseUrl)}\nwire_api = \"responses\"";
            }

            removeTables.RemoveAll(table => table == tablePath);
        }

        warnings.Add("本地模型未生成或复制 GPT/DeepSeek metadata；未被实测的 Plan/Goal/MCP 等能力保持 Untested。Auto Compact 为管理器安全建议值。");
        if (request.TargetSupportsToolUse == false) warnings.Add("所选模型由 LM Studio 声明为未针对 Tool Use 训练，Codex Agent 能力很可能受限。");
        else if (request.TargetSupportsToolUse is null) warnings.Add("fallback Models API 未提供 Tool Use 能力，状态保持 Unknown；建议先运行 Level 2。");
        if (request.TargetSupportsReasoning is null) warnings.Add("未发现可依据的 reasoning capability；未写入 reasoning effort。");
        else if (request.TargetSupportsReasoning == true && allowedReasoningEfforts.Count == 0) warnings.Add("LM Studio 仅报告 on/off reasoning capability，未猜测为 Codex effort；model_reasoning_effort 将不写入。");
    }

    private async Task<CodexInstructionHierarchyProbeResult?> EnsureLmStudioPreflightAsync(
        SwitchRequest request,
        CancellationToken cancellationToken)
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

    internal static SwitchRequest NormalizeSwitchRequest(SwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetModel);
        string? reasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort)
            ? null
            : request.ReasoningEffort.Trim().ToLowerInvariant();
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
                compact = request.AutoCompactTokenLimit ?? suggestedCompact;
                compactMode = request.AutoCompactTokenLimit is null || compact == suggestedCompact
                    ? AutoCompactMode.Automatic
                    : AutoCompactMode.Manual;
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
                compact = request.AutoCompactTokenLimit ??
                    throw new InvalidOperationException("Manual Auto Compact 必须提供明确的 token limit。");
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

    private static Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> BuildSecondaryReplacements(
        SwitchRequest request,
        IReadOnlyList<SecondaryModelOverride> overrides,
        AppSettings settings,
        List<string> warnings)
    {
        Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> result = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? selected = ParseOverrideSelection(request.SecondaryOverrideSelectionJson);
        if (selected is not null)
        {
            HashSet<string> known = overrides.Select(OverrideStateKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] unknown = selected.Where(item => !known.Contains(item)).ToArray();
            if (unknown.Length > 0) throw new InvalidOperationException("Secondary Override 选择在扫描后已失效，请重新加载。");
        }

        if (request.SecondaryOverridePolicy == SecondaryOverridePolicy.FollowMain)
        {
            foreach (SecondaryModelOverride item in overrides.Where(item => IsOverrideSelected(item, selected))) AddReplacement(result, item, new SecondaryOverrideReplacement(request.TargetModel));
            int unselectedExternal = overrides.Count(item => !item.CanEdit && !IsOverrideSelected(item, selected));
            if (unselectedExternal > 0) warnings.Add($"有 {unselectedExternal} 个外部 profile/agent/project override 未显式勾选，保持不变。");
        }
        else if (request.SecondaryOverridePolicy == SecondaryOverridePolicy.RestoreOriginal)
        {
            foreach (SecondaryModelOverride item in overrides.Where(item => IsOverrideSelectedForRestore(item, selected, settings)))
            {
                if (settings.SecondaryOverrideOriginals.TryGetValue(OverrideStateKey(item), out string? original)) AddReplacement(result, item, DecodeSecondaryOriginal(original));
            }
        }

        return result;
    }

    private static void AddReplacement(
        Dictionary<string, Dictionary<string, SecondaryOverrideReplacement>> replacements,
        SecondaryModelOverride item,
        SecondaryOverrideReplacement replacement)
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

    private static string EncodeSecondaryOriginal(SecondaryModelOverride item) => JsonSerializer.Serialize(new SecondaryOriginalValue(item.Model, item.RawTomlValue));

    private static SecondaryOverrideReplacement DecodeSecondaryOriginal(string stored)
    {
        try
        {
            SecondaryOriginalValue? value = JsonSerializer.Deserialize<SecondaryOriginalValue>(stored);
            if (value is not null && !string.IsNullOrEmpty(value.Value)) return new SecondaryOverrideReplacement(value.Value, value.RawTomlValue);
        }
        catch (JsonException)
        {
            // Schema v1 builds stored the semantic model ID directly.
        }

        return new SecondaryOverrideReplacement(stored);
    }

    private static HashSet<string>? ParseOverrideSelection(string? json)
    {
        if (json is null) return null;
        try
        {
            List<SecondaryOverrideTarget> targets = JsonSerializer.Deserialize<List<SecondaryOverrideTarget>>(json) ?? [];
            return targets.Select(target => OverrideStateKey(target.FilePath, target.KeyPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Secondary Override 选择数据无效。", exception);
        }
    }

    private static bool IsOverrideSelected(SecondaryModelOverride item, HashSet<string>? selected) =>
        selected is null ? item.CanEdit : selected.Contains(OverrideStateKey(item));

    private static bool IsOverrideSelectedForRestore(SecondaryModelOverride item, HashSet<string>? selected, AppSettings settings) =>
        settings.SecondaryOverrideOriginals.ContainsKey(OverrideStateKey(item)) && (selected is null || selected.Contains(OverrideStateKey(item)));

    private static bool WasOverrideMutated(SwitchPlan plan, SecondaryModelOverride item)
    {
        string externalKey = $"{Path.GetFullPath(item.FilePath)}::{item.KeyPath}";
        return plan.Mutations.Any(mutation => item.CanEdit
            ? mutation.KeyPath.Equals(item.KeyPath, StringComparison.Ordinal)
            : mutation.KeyPath.Equals(externalKey, StringComparison.OrdinalIgnoreCase));
    }

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
            ProviderKind.LmStudio => request.LmStudioProviderId ?? "lmstudio",
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

        if (request.TargetProvider == ProviderKind.DeepSeek)
        {
            string? catalog = CodexRuntimeProbe.Unquote(read.RootValues.GetValueOrDefault("model_catalog_json"));
            if (string.IsNullOrWhiteSpace(catalog) || !Path.GetFullPath(catalog).Equals(Path.GetFullPath(request.DeepSeekCatalogPath!), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("候选配置语义检查失败：DeepSeek catalog 路径不一致。");
            }
        }
    }

    private static string? GetSavedProviderRoot(AppSettings settings, ProviderKind provider, string key) =>
        settings.ProviderStates.TryGetValue(provider.ToString(), out ProviderState? state)
            ? state.RootValues.GetValueOrDefault(key)
            : null;

    private static ProviderState CaptureProviderState(ProviderKind provider, ConfigReadResult read, string sha)
    {
        if (provider is not (ProviderKind.OpenAI or ProviderKind.DeepSeek))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "只允许持久化 OpenAI 或 DeepSeek provider state。");
        }

        Dictionary<string, string?> roots = ManagedConfigKeys.Root.ToDictionary(key => key, key => read.RootValues.TryGetValue(key, out string? value) ? value : null, StringComparer.Ordinal);
        if (ContainsSensitiveUrlQuery(roots.GetValueOrDefault("openai_base_url")))
        {
            throw new InvalidOperationException("openai_base_url 含疑似凭据查询参数；为避免把 Secret 写入 appsettings，已中止切换。请改用受支持的凭据机制后重试。");
        }

        // Provider tables can contain an official DeepSeek plaintext bearer token.
        // They are deliberately never copied into appsettings; only non-secret root
        // values needed for exact provider-specific restoration are kept.
        Dictionary<string, string?> tables = ManagedConfigKeys.ProviderTables.ToDictionary(key => key, _ => (string?)null, StringComparer.Ordinal);
        return new ProviderState(provider, DateTimeOffset.Now, roots, tables, sha);
    }

    internal static bool ContainsSensitiveUrlQuery(string? rawValue)
    {
        string? value = CodexRuntimeProbe.Unquote(rawValue);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Query)) return false;
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Any(IsSensitiveQueryParameterName);
    }

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

        string[] tokens = decoded.Split(
            decoded.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HashSet<string> sensitiveTokens = new(StringComparer.Ordinal)
        {
            "key", "token", "secret", "password", "credential", "signature",
        };
        if (tokens.Any(sensitiveTokens.Contains))
        {
            return true;
        }

        string compact = string.Concat(decoded.Where(char.IsLetterOrDigit));
        return compact is "key" or "apikey" or "accesstoken" or "token" or "secret" or
            "clientsecret" or "password" or "credential" or "signature";
    }

    private static string? ComposeTableTree(ConfigReadResult read, string parent)
    {
        List<string> parts = [];
        foreach ((string path, string body) in read.TableBodies.Where(pair => pair.Key == parent || pair.Key.StartsWith(parent + ".", StringComparison.Ordinal)).OrderBy(pair => pair.Key.Count(character => character == '.')).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (path == parent) parts.Add(body.TrimEnd('\r', '\n'));
            else parts.Add($"[{path}]\n{body.TrimEnd('\r', '\n')}");
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string BuildCommandProviderBody(string tablePath, string name, string baseUrl, string helperPath, string credentialName) =>
        $"name = {Quote(name)}\nbase_url = {Quote(baseUrl)}\nwire_api = \"responses\"\n\n[{tablePath}.auth]\ncommand = {Quote(Path.GetFullPath(helperPath))}\nargs = [{Quote(credentialName)}]\ntimeout_ms = 5000\nrefresh_interval_ms = 0";

    private static HashSet<string> GetReasoningLevels(JsonElement model)
    {
        if (!model.TryGetProperty("supported_reasoning_levels", out JsonElement levels)) return [];
        return levels.EnumerateArray().Select(item => item.GetProperty("effort").GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static string? GetDefaultReasoning(JsonElement model) => model.TryGetProperty("default_reasoning_level", out JsonElement level) ? level.GetString() : null;

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string DecodeUtf8(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;
        return new UTF8Encoding(false, true).GetString(data);
    }

    private static string OverrideStateKey(SecondaryModelOverride item) => Path.GetFullPath(item.FilePath) + "|" + item.KeyPath;

    private static string OverrideStateKey(string filePath, string keyPath) => Path.GetFullPath(filePath) + "|" + keyPath;

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

    private sealed record SecondaryOriginalValue(string Value, string? RawTomlValue);
}
