using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;
using CodexModelManager.Core.Security;

namespace CodexModelManager.App.UI;

internal sealed class MainController : IDisposable
{
    private const string NoReasoningOverride = "（不写入）";
    private readonly MainForm form;
    private readonly AppComposition services;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<ModelProfile> lmModels = [];
    private int uiActionRunning;
    private AppSettings appSettings = new();
    private bool updating;
    private bool lmRequiresAuthentication;
    private string? credentialHelperPath;
    private string? mcpServerPath;
    private string? currentReasoningEffort;
    private SwitchPlan? lastPlan;
    private GgufChatTemplateAnalysis? templateAnalysis;
    private PromptTemplateRepairPreview? templateRepairPreview;
    private string? templateModelId;

    public MainController(MainForm form, AppComposition services, IAppLogger logger, HttpClient httpClient)
    {
        this.form = form;
        this.services = services;
        this.logger = logger;
        this.httpClient = httpClient;
        logger.MessageLogged += OnLogMessage;
        WireEvents();
    }

    public async Task InitializeAsync()
    {
        await RunUiActionAsync(async () =>
        {
            InstallHelpers();
            RegisterExistingSecrets();
            appSettings = await services.SettingsRepository.LoadAsync(lifetime.Token);
            Uri? configuredEndpoint = Uri.TryCreate(appSettings.LmStudioEndpoint, UriKind.Absolute, out Uri? savedEndpoint) ? savedEndpoint : null;
            LmStudioEndpointDetection detectedEndpoint = await LmStudioEndpointDetector.DetectAsync(configuredEndpoint, lifetime.Token);
            form.LmStudio.EndpointText.Text = detectedEndpoint.Endpoint.AbsoluteUri.TrimEnd('/');
            logger.Info($"LM Studio endpoint discovery: {detectedEndpoint.Endpoint.GetLeftPart(UriPartial.Authority)} via {detectedEndpoint.Source}");
            await RefreshEnvironmentAsync();
            await services.Backups.EnsureInitialSnapshotAsync(lifetime.Token);
            logger.Info("Initial Snapshot 已检查（已有快照不会覆盖）。");
            await RefreshLmStudioAsync();
            await LoadModelsForSelectedProviderAsync();
            await RefreshHistoryAsync();
            RefreshCredentialStatus();
        });
    }

    public void Dispose()
    {
        logger.MessageLogged -= OnLogMessage;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void WireEvents()
    {
        form.Current.RefreshButton.Click += async (_, _) => await RunUiActionAsync(RefreshEnvironmentAsync);
        form.Current.ProviderCombo.SelectedIndexChanged += async (_, _) =>
        {
            if (updating) return;
            InvalidatePreview();
            await RunUiActionAsync(LoadModelsForSelectedProviderAsync);
        };
        form.Current.ModelCombo.SelectedIndexChanged += (_, _) =>
        {
            if (updating) return;
            InvalidatePreview();
            UpdateReasoningChoices();
            SyncLocalSelection();
        };
        form.Current.ReasoningCombo.SelectedIndexChanged += (_, _) => InvalidatePreview();
        form.Current.SecondaryPolicyCombo.SelectedIndexChanged += (_, _) => InvalidatePreview();
        form.Current.SecondaryOverridesList.ItemCheck += (_, _) => BeginInvokePreviewInvalidation();
        form.Current.PreviewButton.Click += async (_, _) => await RunUiActionAsync(PreviewAsync);
        form.Current.SwitchButton.Click += async (_, _) => await RunUiActionAsync(SwitchAsync);

        form.LmStudio.DetectButton.Click += async (_, _) => await RunUiActionAsync(DetectAndRefreshLmStudioAsync);
        form.LmStudio.RefreshModelsButton.Click += async (_, _) => await RunUiActionAsync(RefreshLmStudioAsync);
        form.LmStudio.EndpointText.TextChanged += (_, _) =>
        {
            if (updating) return;
            InvalidatePreview();
            InvalidateLmStudioCompatibilityForSelection();
            InvalidateTemplateAnalysis("Endpoint 已变化，请刷新模型并重新分析。");
        };
        form.LmStudio.ModelCombo.SelectedIndexChanged += (_, _) =>
        {
            if (updating) return;
            InvalidatePreview();
            InvalidateLmStudioCompatibilityForSelection();
            UpdateLocalModelDetails();
            SyncMainLocalSelection();
        };
        form.LmStudio.CodexContextInput.ValueChanged += (_, _) => { InvalidatePreview(); UpdateContextWarning(); };
        form.LmStudio.AutoCompactInput.ValueChanged += (_, _) => { InvalidatePreview(); UpdateContextWarning(); };
        form.LmStudio.BrowseGgufButton.Click += (_, _) => BrowseForGguf();
        form.LmStudio.GgufPathText.TextChanged += (_, _) => InvalidateTemplateAnalysis();
        form.LmStudio.AnalyzeTemplateButton.Click += async (_, _) => await RunUiActionAsync(AnalyzePromptTemplateAsync);
        form.LmStudio.ExportTemplateButton.Click += async (_, _) => await RunUiActionAsync(ExportPromptTemplateAsync);
        form.LmStudio.CopyTemplateButton.Click += async (_, _) => await RunUiActionAsync(CopyPromptTemplateAsync);
        form.LmStudio.RecheckHierarchyButton.Click += async (_, _) => await RunUiActionAsync(RecheckLmStudioHierarchyAsync);

        form.Compatibility.ValidateButton.Click += async (_, _) => await RunUiActionAsync(ValidateCompatibilityAsync);
        form.Compatibility.SmokeButton.Click += async (_, _) => await RunUiActionAsync(RunSmokeTestAsync);
        form.Backups.RefreshButton.Click += async (_, _) => await RunUiActionAsync(RefreshHistoryAsync);
        form.Backups.RestorePreviousButton.Click += async (_, _) => await RunUiActionAsync(RestorePreviousAsync);
        form.Backups.RestoreSelectedButton.Click += async (_, _) => await RunUiActionAsync(RestoreSelectedAsync);
        form.Backups.RestoreInitialButton.Click += async (_, _) => await RunUiActionAsync(RestoreInitialAsync);
        form.Backups.InspectDeepSeekButton.Click += async (_, _) => await RunUiActionAsync(InspectDeepSeekBackupAsync);
        form.SettingsLog.SaveDeepSeekButton.Click += (_, _) => SaveCredential(CredentialNames.DeepSeek, form.SettingsLog.DeepSeekToken);
        form.SettingsLog.SaveLmStudioButton.Click += (_, _) => SaveCredential(CredentialNames.LmStudio, form.SettingsLog.LmStudioToken);
    }

    private async Task RefreshEnvironmentAsync()
    {
        CodexEnvironmentInfo environment = await services.RuntimeProbe.DetectAsync(lifetime.Token);
        services.Backups.CodexVersion = environment.CliVersion;
        updating = true;
        try
        {
            form.Current.CodexVersionValue.Text = $"Desktop {environment.DesktopVersion ?? "未知"} / CLI {environment.CliVersion ?? "未知"}";
            form.Current.CodexStatusValue.Text = environment.IsRunning ? "Running — 请完全关闭后再切换" : "Closed";
            form.Current.CodexStatusValue.ForeColor = environment.IsRunning ? Color.Firebrick : Color.DarkGreen;
            form.Current.CodexHomeValue.Text = environment.CodexHome;
            form.Current.CurrentProviderValue.Text = $"{environment.CurrentProvider} ({environment.CurrentProviderId ?? "unknown"})";
            form.Current.CurrentModelValue.Text = environment.CurrentModel ?? "未显式配置";
            currentReasoningEffort = environment.ReasoningEffort;
            form.Current.SwitchButton.Enabled = !environment.IsRunning && environment.Warning is null;
            form.Current.ProviderCombo.SelectedItem = environment.CurrentProvider is ProviderKind.Unknown ? ProviderKind.OpenAI : environment.CurrentProvider;
            form.Current.OverrideWarningValue.Text = environment.Warning ?? "扫描将在 Preview 时执行。";
            if (environment.Warning is not null) logger.Warning(environment.Warning);
            if (environment.IsRunning) logger.Warning("检测到可能的 Codex/ChatGPT 进程: " + string.Join(", ", environment.RunningProcesses));
            logger.Info($"Codex audit: provider={environment.CurrentProviderId ?? "openai"}, model={environment.CurrentModel ?? "unset"}, configSha={ShortHash(environment.ConfigFingerprint.Sha256)}");
        }
        finally
        {
            updating = false;
        }

        if (environment.Warning is null) await RefreshSecondaryOverridesAsync(environment.ConfigPath);
        else form.Current.SecondaryOverridesList.Items.Clear();
    }

    private async Task RefreshSecondaryOverridesAsync(string configPath)
    {
        HashSet<string> checkedKeys = form.Current.SecondaryOverridesList.CheckedItems
            .OfType<SecondaryOverrideChoice>()
            .Select(choice => choice.StateKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<SecondaryModelOverride> overrides = await services.OverrideScanner.ScanAsync(configPath, lifetime.Token);
        updating = true;
        try
        {
            form.Current.SecondaryOverridesList.Items.Clear();
            foreach (SecondaryModelOverride item in overrides)
            {
                var choice = new SecondaryOverrideChoice(item, appSettings.SecondaryOverrideOriginals.ContainsKey(OverrideStateKey(item)));
                form.Current.SecondaryOverridesList.Items.Add(choice, checkedKeys.Contains(choice.StateKey));
            }
        }
        finally
        {
            updating = false;
        }

        form.Current.OverrideWarningValue.Text = SummarizeOverrides(overrides);
    }

    private async Task RefreshLmStudioAsync()
    {
        if (!Uri.TryCreate(form.LmStudio.EndpointText.Text.Trim(), UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException("LM Studio endpoint 无效。");
        }

        var unauthenticated = new LmStudioClient(endpoint, null, httpClient);
        ProviderProbeResult initial = await unauthenticated.ProbeAsync(lifetime.Token);
        lmRequiresAuthentication = initial.RequiresAuthentication;
        string? token = lmRequiresAuthentication ? GetSecret(CredentialNames.LmStudio) : null;
        var client = lmRequiresAuthentication
            ? new LmStudioClient(endpoint, () => token, httpClient)
            : unauthenticated;
        ProviderProbeResult effective = lmRequiresAuthentication
            ? await client.ProbeAsync(lifetime.Token)
            : initial;
        form.LmStudio.ServerStatusValue.Text = effective.Summary;
        form.LmStudio.ServerStatusValue.ForeColor = effective.IsAvailable ? Color.DarkGreen : Color.Firebrick;
        form.LmStudio.VersionValue.Text = DetectLmStudioVersion() ?? "未知（Models API 未提供版本）";
        lmModels.Clear();
        if (effective.IsAvailable)
        {
            lmModels.AddRange(await client.DiscoverModelsAsync(lifetime.Token));
        }

        updating = true;
        try
        {
            form.LmStudio.ModelCombo.Items.Clear();
            foreach (ModelProfile model in lmModels) form.LmStudio.ModelCombo.Items.Add(model);
            ModelProfile? loaded = lmModels.FirstOrDefault(model => model.IsLoaded == true && model.ModelType?.Equals("llm", StringComparison.OrdinalIgnoreCase) == true);
            if (loaded is not null) form.LmStudio.ModelCombo.SelectedItem = loaded;
        }
        finally
        {
            updating = false;
        }

        InvalidateLmStudioCompatibilityForSelection();
        UpdateLocalModelDetails();
        if (form.LmStudio.ModelCombo.SelectedItem is ModelProfile selectedModel)
        {
            string? resolvedGguf = await LmStudioModelFileLocator.TryResolveAsync(selectedModel, lifetime.Token);
            if (!string.IsNullOrWhiteSpace(resolvedGguf))
            {
                form.LmStudio.GgufPathText.Text = resolvedGguf;
                form.LmStudio.TemplateStatusValue.Text = "已通过 lms ls --json 解析 GGUF；请点击分析。";
            }
        }

        if (form.Current.ProviderCombo.SelectedItem is ProviderKind.LmStudio) await LoadModelsForSelectedProviderAsync();
        appSettings.LmStudioEndpoint = endpoint.AbsoluteUri.TrimEnd('/');
        await services.SettingsRepository.SaveAsync(appSettings, lifetime.Token);
        logger.Info($"LM Studio audit: endpoint={endpoint.GetLeftPart(UriPartial.Authority)}, status={(int?)effective.HttpStatus ?? 0}, models={lmModels.Count}, authRequired={lmRequiresAuthentication}");
    }

    private async Task DetectAndRefreshLmStudioAsync()
    {
        Uri? configured = Uri.TryCreate(form.LmStudio.EndpointText.Text.Trim(), UriKind.Absolute, out Uri? endpoint) ? endpoint : null;
        LmStudioEndpointDetection detection = await LmStudioEndpointDetector.DetectAsync(configured, lifetime.Token);
        form.LmStudio.EndpointText.Text = detection.Endpoint.AbsoluteUri.TrimEnd('/');
        logger.Info($"LM Studio endpoint discovery: {detection.Endpoint.GetLeftPart(UriPartial.Authority)} via {detection.Source}");
        await RefreshLmStudioAsync();
    }

    private async Task LoadModelsForSelectedProviderAsync()
    {
        if (form.Current.ProviderCombo.SelectedItem is not ProviderKind provider) return;
        IReadOnlyList<ModelProfile> models;
        switch (provider)
        {
            case ProviderKind.OpenAI:
                models = await new OpenAiProvider(new CodexAppServerClient(services.HomeProvider.GetCodexHome())).DiscoverModelsAsync(lifetime.Token);
                break;
            case ProviderKind.DeepSeek:
                models = await services.Catalog.GetDeepSeekModelsAsync(lifetime.Token);
                break;
            case ProviderKind.LmStudio:
                models = lmModels.Where(model => model.ModelType is null || model.ModelType.Equals("llm", StringComparison.OrdinalIgnoreCase)).ToArray();
                break;
            default:
                models = [];
                break;
        }

        string currentId = form.Current.CurrentModelValue.Text;
        updating = true;
        try
        {
            form.Current.ModelCombo.Items.Clear();
            foreach (ModelProfile model in models) form.Current.ModelCombo.Items.Add(model);
            ModelProfile? selected = models.FirstOrDefault(model => model.Id == currentId) ?? models.FirstOrDefault(model => model.IsLoaded == true) ?? (models.Count > 0 ? models[0] : null);
            if (selected is not null) form.Current.ModelCombo.SelectedItem = selected;
        }
        finally
        {
            updating = false;
        }

        UpdateReasoningChoices();
    }

    private void UpdateReasoningChoices()
    {
        ModelProfile? model = form.Current.ModelCombo.SelectedItem as ModelProfile;
        string? previous = form.Current.ReasoningCombo.SelectedItem as string;
        updating = true;
        try
        {
            form.Current.ReasoningCombo.Items.Clear();
            if (model?.Provider == ProviderKind.LmStudio) form.Current.ReasoningCombo.Items.Add(NoReasoningOverride);
            foreach (string option in model?.ReasoningOptions ?? [])
            {
                if (option is "on" or "off" || !ManagedConfigKeys.SupportedReasoningEfforts.Contains(option)) continue;
                form.Current.ReasoningCombo.Items.Add(option);
            }

            string? preferred = previous;
            if (preferred is null && model?.Id == form.Current.CurrentModelValue.Text) preferred = currentReasoningEffort;
            preferred ??= model?.DefaultReasoningEffort;
            if (preferred is not null && form.Current.ReasoningCombo.Items.Contains(preferred)) form.Current.ReasoningCombo.SelectedItem = preferred;
            else if (form.Current.ReasoningCombo.Items.Count > 0) form.Current.ReasoningCombo.SelectedIndex = 0;
        }
        finally
        {
            updating = false;
        }
    }

    private void UpdateLocalModelDetails()
    {
        ModelProfile? model = form.LmStudio.ModelCombo.SelectedItem as ModelProfile;
        form.LmStudio.LoadedValue.Text = Bool(model?.IsLoaded);
        form.LmStudio.QuantValue.Text = model is null ? "未知" : $"{model.ModelType ?? "类型未知"} / {model.Quantization ?? "未知"} / {model.Parameters ?? "未知"} / {model.Architecture ?? "架构未知"} / {FormatSize(model.SizeBytes)}";
        form.LmStudio.ToolUseValue.Text = Bool(model?.TrainedForToolUse);
        form.LmStudio.ReasoningValue.Text = Bool(model?.SupportsReasoning);
        form.LmStudio.MaxContextValue.Text = model?.MaxContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "未知";
        form.LmStudio.LoadedContextValue.Text = model?.LoadedContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "未知";
        form.LmStudio.DiscoverySourceValue.Text = model?.Source ?? "未知";
        if (model?.LoadedContextLength is int context)
        {
            int compact = ConfigurationSwitchService.SuggestAutoCompact(context);
            if (appSettings.ModelPreferences.TryGetValue(model.Id, out ModelPreference? preference) &&
                preference.LastLoadedContext == context &&
                preference.AutoCompactTokenLimit is int preferredCompact &&
                preferredCompact > 0 && preferredCompact < context && context - preferredCompact >= 1024)
            {
                compact = preferredCompact;
            }

            updating = true;
            try
            {
                form.LmStudio.CodexContextInput.Value = Math.Clamp(context, (int)form.LmStudio.CodexContextInput.Minimum, (int)form.LmStudio.CodexContextInput.Maximum);
                form.LmStudio.AutoCompactInput.Value = Math.Clamp(compact, (int)form.LmStudio.AutoCompactInput.Minimum, (int)form.LmStudio.AutoCompactInput.Maximum);
            }
            finally
            {
                updating = false;
            }
        }

        UpdateContextWarning();
    }

    private void InvalidateLmStudioCompatibilityForSelection()
    {
        form.LmStudio.HierarchyStatusValue.Text = "Untested";
        form.LmStudio.HierarchyStatusValue.ForeColor = Color.DarkOrange;
        form.LmStudio.HierarchyDetailValue.Text = "当前 loaded instance 尚未执行 instructions + developer + user 差分检测。";
        string? selectedModel = (form.LmStudio.ModelCombo.SelectedItem as ModelProfile)?.Id;
        if (!string.Equals(templateModelId, selectedModel, StringComparison.Ordinal))
        {
            templateModelId = null;
            templateAnalysis = null;
            templateRepairPreview = null;
            form.LmStudio.GgufPathText.Clear();
            form.LmStudio.TemplateStatusValue.Text = "尚未分析";
            form.LmStudio.ExportTemplateButton.Enabled = false;
            form.LmStudio.CopyTemplateButton.Enabled = false;
        }
    }

    private void InvalidateTemplateAnalysis(string detail = "路径已变化，请重新分析。")
    {
        templateAnalysis = null;
        templateRepairPreview = null;
        templateModelId = null;
        form.LmStudio.TemplateStatusValue.Text = detail;
        form.LmStudio.ExportTemplateButton.Enabled = false;
        form.LmStudio.CopyTemplateButton.Enabled = false;
    }

    private void BrowseForGguf()
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "GGUF model (*.gguf)|*.gguf|All files (*.*)|*.*",
            Multiselect = false,
            Title = "选择当前 loaded instance 对应的 GGUF（只读分析）",
        };
        if (File.Exists(form.LmStudio.GgufPathText.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(form.LmStudio.GgufPathText.Text);
            dialog.FileName = Path.GetFileName(form.LmStudio.GgufPathText.Text);
        }

        if (dialog.ShowDialog(form) == DialogResult.OK)
        {
            form.LmStudio.GgufPathText.Text = dialog.FileName;
        }
    }

    private async Task AnalyzePromptTemplateAsync()
    {
        if (form.LmStudio.ModelCombo.SelectedItem is not ModelProfile model || model.IsLoaded != true)
        {
            throw new InvalidOperationException("请选择当前已加载的 LM Studio LLM instance。");
        }

        string path = form.LmStudio.GgufPathText.Text.Trim();
        GgufChatTemplateAnalysis analysis = await services.GgufReader.ReadAsync(path, lifetime.Token);
        if (!string.IsNullOrWhiteSpace(model.Architecture) && !string.IsNullOrWhiteSpace(analysis.Architecture) &&
            !model.Architecture.Equals(analysis.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"所选 GGUF architecture={analysis.Architecture}，但 loaded instance 报告 {model.Architecture}；拒绝为可能错误的模型生成模板。");
        }

        PromptTemplateRepairPreview preview = services.TemplateRepair.CreatePreview(analysis);
        templateAnalysis = analysis;
        templateRepairPreview = preview;
        templateModelId = model.Id;
        form.LmStudio.TemplateStatusValue.Text = $"{preview.Status} | SHA256 {ShortHash(analysis.TemplateSha256)} | {preview.Detail}";
        form.LmStudio.TemplateStatusValue.ForeColor = preview.Status switch
        {
            PromptTemplateRepairStatus.Supported => Color.DarkGreen,
            PromptTemplateRepairStatus.AlreadyCompatible => Color.DarkGreen,
            _ => Color.Firebrick,
        };
        form.LmStudio.ExportTemplateButton.Enabled = preview.Status == PromptTemplateRepairStatus.Supported;
        form.LmStudio.CopyTemplateButton.Enabled = preview.Status == PromptTemplateRepairStatus.Supported;
        logger.Info($"GGUF Prompt Template analysis: model={model.Id}, file={analysis.FileName}, gguf={analysis.GgufVersion}, templateSha={ShortHash(analysis.TemplateSha256)}, repair={preview.Status}");
    }

    private async Task ExportPromptTemplateAsync()
    {
        if (templateAnalysis is null || templateRepairPreview?.Status != PromptTemplateRepairStatus.Supported ||
            form.LmStudio.ModelCombo.SelectedItem is not ModelProfile model || !string.Equals(templateModelId, model.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("请先为当前 loaded instance 分析并确认可修补的 GGUF Prompt Template。");
        }

        (GgufChatTemplateAnalysis currentAnalysis, _) = await RevalidateTemplateAnalysisAsync(model);
        PromptTemplateRepairArtifact artifact = await services.TemplateRepair.ExportAsync(
            currentAnalysis,
            model.Id,
            services.Paths.TemplateFixDirectory,
            lifetime.Token);
        logger.Info($"Prompt Template repair exported: model={model.Id}, directory={artifact.Directory}, originalSha={ShortHash(artifact.OriginalTemplateSha256)}, patchedSha={ShortHash(artifact.PatchedTemplateSha256)}");
        MessageBox.Show(
            form,
            "兼容模板已导出：\n" + artifact.Directory +
            "\n\n请按 APPLY.md 在 LM Studio 中手动应用，保存后手动卸载并重新加载模型，再点击“重新检测 Codex 指令层级”。管理器不会自动修改 LM Studio 或 GGUF。",
            "Prompt Template 已导出",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task CopyPromptTemplateAsync()
    {
        if (form.LmStudio.ModelCombo.SelectedItem is not ModelProfile model ||
            templateAnalysis is null ||
            templateRepairPreview?.Status != PromptTemplateRepairStatus.Supported ||
            !string.Equals(templateModelId, model.Id, StringComparison.Ordinal))
        {
            MessageBox.Show(form, "请先分析并确认模板可安全修补。", "Prompt Template", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        (_, PromptTemplateRepairPreview currentPreview) = await RevalidateTemplateAnalysisAsync(model);
        Clipboard.SetText(currentPreview.PatchedTemplate!);
        logger.Info("Codex-compatible Prompt Template 已由用户复制到剪贴板（模板正文未记录）。");
        MessageBox.Show(form, "兼容模板已复制。请在 LM Studio 的目标模型 Prompt Template override 中完整粘贴。", "Prompt Template", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task RecheckLmStudioHierarchyAsync()
    {
        if (form.LmStudio.ModelCombo.SelectedItem is not ModelProfile selected || selected.IsLoaded != true)
        {
            throw new InvalidOperationException("请选择当前已加载的 LM Studio LLM instance。");
        }

        if (!Uri.TryCreate(form.LmStudio.EndpointText.Text.Trim(), UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException("LM Studio endpoint 无效。");
        }

        var unauthenticatedClient = new LmStudioClient(endpoint, null, httpClient);
        ProviderProbeResult unauthenticatedProbe = await unauthenticatedClient.ProbeAsync(lifetime.Token);
        lmRequiresAuthentication = unauthenticatedProbe.RequiresAuthentication;
        string? token = lmRequiresAuthentication ? GetSecret(CredentialNames.LmStudio) : null;
        var client = lmRequiresAuthentication
            ? new LmStudioClient(endpoint, () => token, httpClient)
            : unauthenticatedClient;
        ProviderProbeResult effectiveProbe = lmRequiresAuthentication
            ? await client.ProbeAsync(lifetime.Token)
            : unauthenticatedProbe;
        if (!effectiveProbe.IsAvailable)
        {
            throw new InvalidOperationException("LM Studio preflight 失败: " + effectiveProbe.Summary);
        }

        IReadOnlyList<ModelProfile> currentModels = await client.DiscoverModelsAsync(lifetime.Token);
        ModelProfile? current = currentModels.FirstOrDefault(model => model.Id == selected.Id && model.IsLoaded == true);
        if (current is null)
        {
            throw new InvalidOperationException("所选 loaded instance 已卸载、删除或 instance ID 已变化，请刷新模型列表。");
        }

        var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, lmRequiresAuthentication ? () => token : null);
        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync(current.Id, lifetime.Token);
        DisplayHierarchyProbe(result);
        logger.Info($"LM Studio instruction hierarchy: model={current.Id}, compatible={result.IsCompatible}, code={result.FailureCode ?? "none"}, control={result.ControlHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}, hierarchy={result.HierarchyHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
        MessageBox.Show(
            form,
            result.IsCompatible
                ? "普通 Responses 与 Codex instructions/developer/user 请求均已通过。正式切换时仍会再次实时验证。"
                : $"检测失败 [{result.FailureCode ?? CompatibilityFailureCodes.OtherProviderError}]：{result.Detail}\n\n若已应用兼容模板，请确认已经手动卸载并重新加载模型。",
            result.IsCompatible ? "Codex 指令层级 PASS" : "Codex 指令层级 FAILED",
            MessageBoxButtons.OK,
            result.IsCompatible ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private async Task<(GgufChatTemplateAnalysis Analysis, PromptTemplateRepairPreview Preview)> RevalidateTemplateAnalysisAsync(ModelProfile model)
    {
        if (templateAnalysis is null || !string.Equals(templateModelId, model.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("当前模型的 GGUF Prompt Template 尚未完成分析。");
        }

        GgufChatTemplateAnalysis current = await services.GgufReader.ReadAsync(templateAnalysis.FilePath, lifetime.Token);
        if (current.FileLength != templateAnalysis.FileLength ||
            current.LastWriteTimeUtc != templateAnalysis.LastWriteTimeUtc ||
            !current.TemplateSha256.Equals(templateAnalysis.TemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateTemplateAnalysis();
            throw new IOException("GGUF 或其 Prompt Template 在分析后发生变化；请重新分析，未导出/复制旧模板。");
        }

        PromptTemplateRepairPreview currentPreview = services.TemplateRepair.CreatePreview(current);
        if (currentPreview.Status != PromptTemplateRepairStatus.Supported || currentPreview.PatchedTemplate is null)
        {
            InvalidateTemplateAnalysis();
            throw new InvalidDataException("重新读取后模板不再满足精确修补规则；请重新分析。");
        }

        templateAnalysis = current;
        templateRepairPreview = currentPreview;
        return (current, currentPreview);
    }

    private void DisplayHierarchyProbe(CodexInstructionHierarchyProbeResult result)
    {
        bool templateFixRequired = result.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
        form.LmStudio.HierarchyStatusValue.Text = result.IsCompatible ? "PASS" : templateFixRequired ? "Template Fix Required" : "FAILED";
        form.LmStudio.HierarchyStatusValue.ForeColor = result.IsCompatible ? Color.DarkGreen : Color.Firebrick;
        form.LmStudio.HierarchyDetailValue.Text =
            $"Control {FormatProbeStatus(result.ControlPassed, result.ControlHttpStatus)}；Codex-shaped {FormatProbeStatus(result.HierarchyPassed, result.HierarchyHttpStatus)}；Failure Code: {result.FailureCode ?? "none"}。{result.Detail}";
        CompatibilityResult[] results =
        [
            new CompatibilityResult("Responses", result.ControlPassed ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, result.ControlPassed ? "普通 Responses control 请求成功。" : result.Detail, result.CheckedAt, result.ControlPassed ? null : result.FailureCode),
            new CompatibilityResult("Codex Instruction Hierarchy", result.IsCompatible ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, result.Detail, result.CheckedAt, result.IsCompatible ? null : result.FailureCode),
            new CompatibilityResult("Codex Agent", result.IsCompatible ? CompatibilityStatus.Untested : CompatibilityStatus.Failed, result.IsCompatible ? "需要继续执行 Level 3 临时 Codex Agent 测试。" : "基础指令层级失败，当前 loaded instance 无法可靠运行 Codex Agent。", result.CheckedAt, result.IsCompatible ? null : result.FailureCode),
        ];
        DisplayCompatibility(results);
    }

    private void UpdateContextWarning()
    {
        ModelProfile? model = form.LmStudio.ModelCombo.SelectedItem as ModelProfile;
        if (model?.IsLoaded != true || model.LoadedContextLength is null)
        {
            form.LmStudio.ContextWarningValue.Text = "模型未加载或 fallback 未提供实际 context，禁止安全切换。";
            form.LmStudio.ContextWarningValue.ForeColor = Color.Firebrick;
            return;
        }

        int codex = (int)form.LmStudio.CodexContextInput.Value;
        int compact = (int)form.LmStudio.AutoCompactInput.Value;
        if (codex != model.LoadedContextLength)
        {
            form.LmStudio.ContextWarningValue.Text = "Codex Context 必须等于 Loaded Context；理论 Max 不能替代实际值。";
            form.LmStudio.ContextWarningValue.ForeColor = Color.Firebrick;
        }
        else if (compact <= 0 || compact >= codex || codex - compact < 1024)
        {
            form.LmStudio.ContextWarningValue.Text = "Auto Compact 必须小于 context，并至少保留 1,024 tokens 余量。";
            form.LmStudio.ContextWarningValue.ForeColor = Color.Firebrick;
        }
        else
        {
            form.LmStudio.ContextWarningValue.Text = $"一致：Max {model.MaxContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "未知"} / Loaded {codex:N0} / Codex {codex:N0}";
            form.LmStudio.ContextWarningValue.ForeColor = Color.DarkGreen;
        }
    }

    private void SyncLocalSelection()
    {
        if (form.Current.ProviderCombo.SelectedItem is not ProviderKind.LmStudio || form.Current.ModelCombo.SelectedItem is not ModelProfile selected) return;
        ModelProfile? local = lmModels.FirstOrDefault(model => model.Id == selected.Id);
        if (local is null) return;
        updating = true;
        form.LmStudio.ModelCombo.SelectedItem = local;
        updating = false;
        InvalidateLmStudioCompatibilityForSelection();
        UpdateLocalModelDetails();
    }

    private void SyncMainLocalSelection()
    {
        if (form.Current.ProviderCombo.SelectedItem is not ProviderKind.LmStudio || form.LmStudio.ModelCombo.SelectedItem is not ModelProfile selected) return;
        foreach (object item in form.Current.ModelCombo.Items)
        {
            if (item is ModelProfile model && model.Id == selected.Id)
            {
                updating = true;
                form.Current.ModelCombo.SelectedItem = item;
                updating = false;
                UpdateReasoningChoices();
                break;
            }
        }
    }

    private async Task PreviewAsync()
    {
        SwitchRequest request = await CreateRequestAsync();
        lastPlan = await services.Switches.CreatePlanAsync(request, lifetime.Token);
        form.Current.OverrideWarningValue.Text = SummarizeOverrides(lastPlan.SecondaryOverrides);
        ShowPlan(lastPlan, false);
    }

    private async Task SwitchAsync()
    {
        SwitchRequest request = await CreateRequestAsync();
        SwitchPlan plan = await services.Switches.CreatePlanAsync(request, lifetime.Token);
        lastPlan = plan;
        if (ShowPlan(plan, true) != DialogResult.Yes) return;
        SwitchRequest confirmedRequest = await CreateRequestAsync();
        if (confirmedRequest != plan.Request) throw new IOException("Provider/模型/context 在确认期间发生变化，请刷新并重新预览。");
        await services.Switches.CommitAsync(plan, lifetime.Token);
        appSettings = await services.SettingsRepository.LoadAsync(lifetime.Token);
        logger.Info($"切换完成: provider={plan.Request.TargetProvider}, model={plan.Request.TargetModel}, files={plan.Files.Count}");
        MessageBox.Show(form, "切换完成，请重新启动 Codex。", "Codex Multi-Model Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshEnvironmentAsync();
        await RefreshHistoryAsync();
    }

    private async Task<SwitchRequest> CreateRequestAsync()
    {
        if (form.Current.ProviderCombo.SelectedItem is not ProviderKind provider || form.Current.ModelCombo.SelectedItem is not ModelProfile model)
        {
            throw new InvalidOperationException("请选择目标 Provider 和 Model。");
        }

        string? reasoning = form.Current.ReasoningCombo.SelectedItem as string;
        if (reasoning == NoReasoningOverride) reasoning = null;
        SecondaryOverridePolicy policy = form.Current.SecondaryPolicyCombo.SelectedItem is SecondaryOverridePolicy selectedPolicy ? selectedPolicy : SecondaryOverridePolicy.Preserve;
        string? overrideSelection = null;
        if (policy is SecondaryOverridePolicy.FollowMain or SecondaryOverridePolicy.RestoreOriginal)
        {
            SecondaryOverrideTarget[] selectedOverrides = form.Current.SecondaryOverridesList.CheckedItems
                .OfType<SecondaryOverrideChoice>()
                .Select(choice => new SecondaryOverrideTarget(choice.Override.FilePath, choice.Override.KeyPath))
                .OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.KeyPath, StringComparer.Ordinal)
                .ToArray();
            overrideSelection = JsonSerializer.Serialize(selectedOverrides);
        }

        string? catalog = provider == ProviderKind.DeepSeek ? await services.Catalog.EnsureDeepSeekCatalogAsync(lifetime.Token) : null;
        Uri? endpoint = null;
        string? lmProvider = null;
        int? context = null;
        int? compact = null;
        string? allowedCodexReasoningEfforts = null;
        bool requestRequiresAuthentication = false;
        if (provider == ProviderKind.LmStudio)
        {
            if (!Uri.TryCreate(form.LmStudio.EndpointText.Text.Trim(), UriKind.Absolute, out endpoint)) throw new InvalidOperationException("LM Studio endpoint 无效。");
            var unauthenticatedClient = new LmStudioClient(endpoint, null, httpClient);
            ProviderProbeResult unauthenticatedProbe = await unauthenticatedClient.ProbeAsync(lifetime.Token);
            lmRequiresAuthentication = unauthenticatedProbe.RequiresAuthentication;
            requestRequiresAuthentication = lmRequiresAuthentication;
            string? token = lmRequiresAuthentication ? GetSecret(CredentialNames.LmStudio) : null;
            var preflightClient = lmRequiresAuthentication
                ? new LmStudioClient(endpoint, () => token, httpClient)
                : unauthenticatedClient;
            ProviderProbeResult preflight = lmRequiresAuthentication
                ? await preflightClient.ProbeAsync(lifetime.Token)
                : unauthenticatedProbe;
            if (!preflight.IsAvailable) throw new InvalidOperationException("LM Studio preflight 失败: " + preflight.Summary);
            IReadOnlyList<ModelProfile> currentModels = await preflightClient.DiscoverModelsAsync(lifetime.Token);
            model = currentModels.FirstOrDefault(item => item.Id == model.Id) ?? throw new InvalidOperationException("所选 LM Studio 模型已被删除或 instance ID 已变化，请刷新模型列表。");
            if (model.IsLoaded != true || model.LoadedContextLength is null) throw new InvalidOperationException("所选 LM Studio 模型已卸载或实际 context 未知。");
            allowedCodexReasoningEfforts = ReasoningEffortPolicy.CanonicalizeAllowed(model.ReasoningOptions);
            IReadOnlySet<string> allowed = ReasoningEffortPolicy.ParseAllowed(allowedCodexReasoningEfforts);
            if (!string.IsNullOrWhiteSpace(reasoning) && !allowed.Contains(reasoning))
            {
                reasoning = null;
                updating = true;
                try
                {
                    form.Current.ReasoningCombo.SelectedItem = NoReasoningOverride;
                }
                finally
                {
                    updating = false;
                }

                logger.Warning("LM Studio 当前模型未报告与 Codex 精确匹配的 reasoning effort；已强制改为不写入。");
            }

            context = (int)form.LmStudio.CodexContextInput.Value;
            compact = (int)form.LmStudio.AutoCompactInput.Value;
            if (context != model.LoadedContextLength) throw new InvalidOperationException("Codex Local Context 必须与 LM Studio 实际 Loaded Context 一致。");
            bool redirected = Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(key => key.ToString()).Any(key => key?.StartsWith("CODEX_OSS_", StringComparison.OrdinalIgnoreCase) == true);
            lmProvider = endpoint.IsLoopback && endpoint.Port == 1234 && !lmRequiresAuthentication && !redirected ? "lmstudio" : "lmstudio_local_cmm";
        }
        else if (provider == ProviderKind.OpenAI)
        {
            var appServer = new CodexAppServerClient(services.HomeProvider.GetCodexHome());
            IReadOnlyList<ModelProfile> currentModels = await appServer.ListModelsAsync(lifetime.Token);
            model = currentModels.FirstOrDefault(item => item.Id == model.Id) ?? throw new InvalidOperationException("所选 OpenAI 模型已不在当前 App Server catalog，请刷新模型列表。");
            if (reasoning is not null && !(model.ReasoningOptions ?? []).Contains(reasoning, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("所选 reasoning effort 已不被当前模型支持，请刷新模型列表。");
            }
        }

        return new SwitchRequest(provider, model.Id, reasoning, context, compact, policy, lmProvider, endpoint, requestRequiresAuthentication, credentialHelperPath, catalog, model.TrainedForToolUse, model.SupportsReasoning, model.ModelType, overrideSelection, allowedCodexReasoningEfforts);
    }

    private async Task ValidateCompatibilityAsync()
    {
        SwitchRequest request = await CreateRequestAsync();
        if (request.TargetProvider == ProviderKind.DeepSeek && MessageBox.Show(form, "DeepSeek 在线兼容性测试会发送少量 API 请求，可能产生费用。继续吗？", "确认测试", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        IModelProvider provider = request.TargetProvider switch
        {
            ProviderKind.OpenAI => new OpenAiProvider(new CodexAppServerClient(services.HomeProvider.GetCodexHome())),
            ProviderKind.DeepSeek => new DeepSeekProvider(services.Catalog, () => GetSecret(CredentialNames.DeepSeek), httpClient),
            ProviderKind.LmStudio => new LmStudioClient(
                request.LmStudioEndpoint!,
                request.LmStudioRequiresAuthentication ? () => GetSecret(CredentialNames.LmStudio) : null,
                httpClient),
            _ => throw new InvalidOperationException("unknown provider"),
        };
        CompatibilityReport report = await provider.TestCompatibilityAsync(request.TargetModel, lifetime.Token);
        DisplayCompatibility(report.Results);
        if (request.TargetProvider == ProviderKind.LmStudio)
        {
            DisplayHierarchyCompatibilityResult(report.Results);
        }

        logger.Info($"Compatibility test: provider={request.TargetProvider}, model={request.TargetModel}, results={report.Results.Count}");
    }

    private async Task RunSmokeTestAsync()
    {
        SwitchRequest request = await CreateRequestAsync();
        if (request.TargetProvider == ProviderKind.LmStudio)
        {
            CodexInstructionHierarchyProbeResult hierarchy = await services.LmStudioPreflight.ProbeAsync(request, lifetime.Token);
            if (!hierarchy.IsCompatible)
            {
                throw new LmStudioCompatibilityException(hierarchy);
            }
        }

        string cost = request.TargetProvider == ProviderKind.DeepSeek ? "这会调用 DeepSeek API，可能产生费用。" : "本地 LM Studio 不产生云 API 费用。";
        if (MessageBox.Show(form, $"将在独立 %TEMP% 目录启动真实 Codex Agent。\n{cost}\n不会复制 auth.json，不会修改真实工程。继续吗？", "Full Smoke Test", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (credentialHelperPath is null || mcpServerPath is null) throw new InvalidOperationException("测试 Helper 尚未安装。");
        var smoke = new CodexSmokeTestService(credentialHelperPath, mcpServerPath);
        SmokeTestResult result = await smoke.RunAsync(request, lifetime.Token);
        DisplayCompatibility(result.Results);
        logger.Info($"Codex smoke test: passed={result.Passed}, directory={result.Directory}");
        MessageBox.Show(form, result.Summary + "\n临时目录：" + result.Directory, "Smoke Test", MessageBoxButtons.OK, result.Passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void DisplayCompatibility(IEnumerable<CompatibilityResult> results)
    {
        form.Compatibility.Results.Rows.Clear();
        foreach (CompatibilityResult item in results) form.Compatibility.Results.Rows.Add(item.Capability, item.Status, item.FailureCode ?? string.Empty, item.Detail);
    }

    private void DisplayHierarchyCompatibilityResult(IEnumerable<CompatibilityResult> results)
    {
        CompatibilityResult? hierarchy = results.FirstOrDefault(item => item.Capability == "Codex Instruction Hierarchy");
        if (hierarchy is null)
        {
            return;
        }

        bool templateFixRequired = hierarchy.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
        form.LmStudio.HierarchyStatusValue.Text = hierarchy.Status == CompatibilityStatus.Supported ? "PASS" : templateFixRequired ? "Template Fix Required" : "FAILED";
        form.LmStudio.HierarchyStatusValue.ForeColor = hierarchy.Status == CompatibilityStatus.Supported ? Color.DarkGreen : Color.Firebrick;
        form.LmStudio.HierarchyDetailValue.Text = $"Failure Code: {hierarchy.FailureCode ?? "none"}。{hierarchy.Detail}";
    }

    private async Task RefreshHistoryAsync()
    {
        IReadOnlyList<BackupSnapshotInfo> history = await services.Backups.ListHistoryAsync(lifetime.Token);
        form.Backups.History.Items.Clear();
        foreach (BackupSnapshotInfo snapshot in history)
        {
            string source = $"{snapshot.Manifest.SourceProvider}/{snapshot.Manifest.SourceModel}";
            string target = $"{snapshot.Manifest.TargetProvider}/{snapshot.Manifest.TargetModel}";
            var item = new ListViewItem([snapshot.Manifest.CreatedAt, snapshot.Manifest.Operation.ToString(), source, target, snapshot.HashesValid ? "OK" : "INVALID"])
            {
                Tag = snapshot,
                ForeColor = snapshot.HashesValid ? SystemColors.WindowText : Color.Firebrick,
            };
            form.Backups.History.Items.Add(item);
        }
    }

    private async Task RestorePreviousAsync()
    {
        IReadOnlyList<BackupSnapshotInfo> history = await services.Backups.ListHistoryAsync(lifetime.Token);
        BackupSnapshotInfo? latest = history.FirstOrDefault(snapshot => snapshot.HashesValid);
        if (latest is null) throw new InvalidOperationException("没有可恢复的历史快照。");
        await RestoreAsync(latest.Directory, "恢复上一次");
    }

    private async Task RestoreSelectedAsync()
    {
        if (form.Backups.History.SelectedItems.Count != 1 || form.Backups.History.SelectedItems[0].Tag is not BackupSnapshotInfo snapshot) throw new InvalidOperationException("请选择一个有效历史快照。");
        if (!snapshot.HashesValid) throw new InvalidDataException("所选快照 SHA 校验失败。");
        await RestoreAsync(snapshot.Directory, "恢复所选快照");
    }

    private Task RestoreInitialAsync() => RestoreAsync(Path.Combine(services.Backups.BackupRoot, "initial"), "恢复 Initial Snapshot");

    private async Task RestoreAsync(string directory, string title)
    {
        CodexEnvironmentInfo environment = await services.RuntimeProbe.DetectAsync(lifetime.Token);
        if (environment.IsRunning) throw new InvalidOperationException("恢复前必须完全关闭 Codex Desktop。");
        if (MessageBox.Show(form, $"{title}？恢复前会先备份当前状态，因此本操作可逆。", "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        environment = await services.RuntimeProbe.DetectAsync(lifetime.Token);
        if (environment.IsRunning) throw new InvalidOperationException("确认期间检测到 Codex Desktop 已启动；恢复已中止。");
        await services.Backups.RestoreAsync(directory, lifetime.Token);
        logger.Info("已恢复备份: " + directory);
        MessageBox.Show(form, "恢复完成，请重新启动 Codex。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshEnvironmentAsync();
        await RefreshHistoryAsync();
    }

    private async Task InspectDeepSeekBackupAsync()
    {
        string directory = Path.Combine(services.HomeProvider.GetCodexHome(), "backup-deepseek");
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(form, "未发现 DeepSeek 官方 backup-deepseek。", "backup-deepseek", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var builder = new StringBuilder().AppendLine(directory).AppendLine();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            FileFingerprint fingerprint = await FileFingerprintService.CaptureAsync(file, lifetime.Token);
            builder.Append(Path.GetFileName(file)).Append("  ")
                .Append(File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)).Append("  ")
                .Append(fingerprint.Length.ToString("N0", CultureInfo.CurrentCulture)).Append(" bytes  SHA256 ")
                .AppendLine(ShortHash(fingerprint.Sha256));
        }

        builder.AppendLine().Append("仅显示名称、大小与哈希；本工具不会读取展示、删除、移动、覆盖或重命名这些文件。");
        MessageBox.Show(form, builder.ToString(), "DeepSeek 官方 backup-deepseek", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveCredential(string target, TextBox input)
    {
        string secret = input.Text;
        if (string.IsNullOrWhiteSpace(secret))
        {
            MessageBox.Show(form, "Token 不能为空。", "凭据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        services.Redactor.Register(secret);
        services.SecretStore.Save(target, secret.AsSpan());
        input.Clear();
        logger.Info($"Credential Manager 凭据已更新: {target}（值未记录）");
        RefreshCredentialStatus();
    }

    private void RefreshCredentialStatus()
    {
        form.SettingsLog.CredentialStatus.Text = $"凭据状态：DeepSeek {(services.SecretStore.Exists(CredentialNames.DeepSeek) ? "已配置" : "未配置")}；LM Studio {(services.SecretStore.Exists(CredentialNames.LmStudio) ? "已配置" : "未配置")}";
    }

    private void RegisterExistingSecrets()
    {
        services.Redactor.Register(GetSecret(CredentialNames.DeepSeek));
        services.Redactor.Register(GetSecret(CredentialNames.LmStudio));
    }

    private string? GetSecret(string name)
    {
        try { return services.SecretStore.Read(name); } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { logger.Warning($"Credential Manager 读取失败: {name} ({exception.GetType().Name})"); return null; }
    }

    private DialogResult ShowPlan(SwitchPlan plan, bool confirmation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Changed:");
        foreach (ConfigMutation mutation in plan.Mutations)
        {
            string oldValue = mutation.IsSecret ? "<redacted>" : mutation.OldValue ?? "<absent>";
            string newValue = mutation.IsSecret ? "<redacted>" : mutation.NewValue ?? "<removed>";
            builder.Append("  ").Append(mutation.Kind).Append(' ').Append(mutation.KeyPath).Append(": ").Append(oldValue).Append(" -> ").AppendLine(newValue);
        }

        builder.AppendLine().AppendLine("Preserved:")
            .Append("  MCP servers: ").AppendLine(plan.Preservation.McpServerCount.ToString(CultureInfo.InvariantCulture))
            .Append("  Projects: ").AppendLine(plan.Preservation.ProjectCount.ToString(CultureInfo.InvariantCulture))
            .Append("  Hooks sections: ").AppendLine(plan.Preservation.HookSectionCount.ToString(CultureInfo.InvariantCulture))
            .Append("  Plugins sections: ").AppendLine(plan.Preservation.PluginSectionCount.ToString(CultureInfo.InvariantCulture));
        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine().AppendLine("Warnings:");
            foreach (string warning in plan.Warnings) builder.Append("  - ").AppendLine(warning);
        }

        if (plan.LmStudioPreflight is CodexInstructionHierarchyProbeResult preflight)
        {
            builder.AppendLine().AppendLine("LM Studio Preflight:")
                .Append("  Control: ").AppendLine(FormatProbeStatus(preflight.ControlPassed, preflight.ControlHttpStatus))
                .Append("  Codex Instruction Hierarchy: ").AppendLine(FormatProbeStatus(preflight.HierarchyPassed, preflight.HierarchyHttpStatus));
        }

        string text = services.Redactor.Redact(builder.ToString());
        return MessageBox.Show(form, text, confirmation ? "确认 Switch Model" : "Preview Changes", confirmation ? MessageBoxButtons.YesNo : MessageBoxButtons.OK, confirmation ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void InstallHelpers()
    {
        credentialHelperPath = InstallHelper("credential", "CodexModelManager.CredentialHelper", "CodexModelManager.CredentialHelper.exe");
        mcpServerPath = InstallHelper("mcp", "CodexModelManager.TestMcpServer", "CodexModelManager.TestMcpServer.exe");
        logger.Info($"Helper status: credential={(credentialHelperPath is null ? "missing" : "ready")}, mcp={(mcpServerPath is null ? "missing" : "ready")}");
    }

    private string? InstallHelper(string publishSubdirectory, string projectName, string executableName)
    {
        string? sourceDirectory = FindHelperDirectory(publishSubdirectory, projectName, executableName);
        string targetDirectory = Path.Combine(services.Paths.BinDirectory, publishSubdirectory);
        Directory.CreateDirectory(targetDirectory);
        if (sourceDirectory is not null)
        {
            foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".json"))
            {
                CopyAtomically(source, Path.Combine(targetDirectory, Path.GetFileName(source)));
            }
        }

        string target = Path.Combine(targetDirectory, executableName);
        return File.Exists(target) ? target : null;
    }

    private static string? FindHelperDirectory(string publishSubdirectory, string projectName, string executableName)
    {
        string published = Path.Combine(AppContext.BaseDirectory, "helpers", publishSubdirectory);
        if (File.Exists(Path.Combine(published, executableName))) return published;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string debug = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", projectName, "bin", configuration, "net8.0"));
        return File.Exists(Path.Combine(debug, executableName)) ? debug : null;
    }

    private static void CopyAtomically(string source, string destination)
    {
        byte[] sourceBytes = File.ReadAllBytes(source);
        if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(sourceBytes)) return;
        string temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, sourceBytes);
        if (File.Exists(destination)) File.Replace(temp, destination, null, true); else File.Move(temp, destination);
    }

    private static string? DetectLmStudioVersion()
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string product = process.MainModule?.FileVersionInfo.ProductName ?? string.Empty;
                    if (process.ProcessName.Contains("lm studio", StringComparison.OrdinalIgnoreCase) || product.Contains("LM Studio", StringComparison.OrdinalIgnoreCase))
                    {
                        return process.MainModule?.FileVersionInfo.ProductVersion;
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }

        return null;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        if (Interlocked.CompareExchange(ref uiActionRunning, 1, 0) != 0)
        {
            logger.Warning("已有操作正在执行，本次重复操作已忽略。");
            return;
        }

        form.UseWaitCursor = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (LmStudioCompatibilityException exception)
        {
            DisplayHierarchyProbe(exception.Result);
            logger.Warning($"LM Studio compatibility blocked: code={exception.Result.FailureCode ?? CompatibilityFailureCodes.OtherProviderError}, control={exception.Result.ControlHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}, hierarchy={exception.Result.HierarchyHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            MessageBox.Show(form, services.Redactor.Redact(exception.Message), "LM Studio Prompt Template 不兼容", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception exception)
        {
            logger.LogError("操作失败", exception);
            MessageBox.Show(form, services.Redactor.Redact($"{exception.GetType().Name}: {exception.Message}"), "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            form.UseWaitCursor = false;
            Volatile.Write(ref uiActionRunning, 0);
        }
    }

    private void OnLogMessage(object? sender, string message)
    {
        if (form.IsDisposed) return;
        void Append() => form.SettingsLog.Log.AppendText(message + Environment.NewLine);
        if (form.InvokeRequired) form.BeginInvoke((Action)Append); else Append();
    }

    private void InvalidatePreview() => lastPlan = null;

    private static string FormatProbeStatus(bool passed, int? status) => $"{(passed ? "PASS" : "FAILED")} (HTTP {status?.ToString(CultureInfo.InvariantCulture) ?? "未返回"})";

    private void BeginInvokePreviewInvalidation()
    {
        if (updating || form.IsDisposed) return;
        form.BeginInvoke((Action)InvalidatePreview);
    }

    private static string Bool(bool? value) => value switch { true => "Yes", false => "No", null => "未知" };
    private static string FormatSize(long? bytes) => bytes is long value ? $"{value / 1024d / 1024d / 1024d:F1} GiB" : "大小未知";
    private static string ShortHash(string value) => string.IsNullOrEmpty(value) ? "missing" : value[..Math.Min(12, value.Length)];
    private static string SummarizeOverrides(IReadOnlyList<SecondaryModelOverride> overrides) => overrides.Count == 0
        ? "未发现 Secondary Model Overrides。"
        : $"发现 {overrides.Count} 项，其中 {overrides.Count(item => item.IsPotentialCloudRequest)} 项可能访问云 Provider；默认不修改，只有勾选项才参与 FollowMain/RestoreOriginal。";

    private static string OverrideStateKey(SecondaryModelOverride item) => Path.GetFullPath(item.FilePath) + "|" + item.KeyPath;

    private sealed class SecondaryOverrideChoice
    {
        private readonly bool canRestore;

        public SecondaryOverrideChoice(SecondaryModelOverride item, bool canRestore)
        {
            Override = item;
            StateKey = OverrideStateKey(item);
            this.canRestore = canRestore;
        }

        public SecondaryModelOverride Override { get; }

        public string StateKey { get; }

        public override string ToString()
        {
            SecondaryModelOverride item = Override;
            string scope = item.CanEdit ? "主配置" : "外部文件";
            string cloud = item.IsPotentialCloudRequest ? "可能云调用" : "Provider 未判定为云";
            string restore = canRestore ? " / 有原值" : string.Empty;
            return $"[{scope} / {cloud}{restore}] {item.FilePath} :: {item.KeyPath} = {item.Model}";
        }
    }
}
