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
    private bool lmStudioRecoveryPending;
    private bool lmStudioLifecycleBusy;
    private readonly Dictionary<Control, bool> lmStudioLifecycleControlStates = [];

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
            await RecoverIncompleteLmStudioTransactionsAsync();
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
        form.LmStudio.RecoverTransactionButton.Click += async (_, _) => await RunUiActionAsync(() => RecoverIncompleteLmStudioTransactionsAsync(showNoPendingMessage: true));
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
            form.Current.PreviewButton.Enabled = environment.Warning is null;
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
        ApplyLmStudioRecoveryGate();
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
                form.LmStudio.TemplateStatusValue.Text = "已通过 lms ls --json --variants 解析精确 GGUF；请点击分析。";
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
        DisplayProbeStep(form.LmStudio.BasicControlValue, null);
        DisplayProbeStep(form.LmStudio.LeadingDeveloperValue, null);
        DisplayProbeStep(form.LmStudio.ConversationControlValue, null);
        DisplayProbeStep(form.LmStudio.ContinuationDeveloperValue, null);
        form.LmStudio.HierarchyDetailValue.Text = "当前 loaded instance 尚未执行四阶段差分检测。";
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
            PromptTemplateRepairStatus.UpgradeRequired => Color.DarkOrange,
            PromptTemplateRepairStatus.AlreadyCompatible => Color.DarkGreen,
            _ => Color.Firebrick,
        };
        form.LmStudio.ExportTemplateButton.Enabled = preview.Status is PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired;
        form.LmStudio.CopyTemplateButton.Enabled = preview.Status is PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired;
        logger.Info($"GGUF Prompt Template analysis: model={model.Id}, file={analysis.FileName}, gguf={analysis.GgufVersion}, templateSha={ShortHash(analysis.TemplateSha256)}, repair={preview.Status}");
    }

    private async Task ExportPromptTemplateAsync()
    {
        if (templateAnalysis is null || templateRepairPreview?.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired) ||
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
            "\n\n这是手工回退工件：可按 APPLY.md 在 LM Studio 中应用并手动重载。主 Switch 流程仅对受支持失败码提供经预览确认的事务式运行时重载；两种方式都不会修改 GGUF。",
            "Prompt Template 已导出",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task CopyPromptTemplateAsync()
    {
        if (form.LmStudio.ModelCombo.SelectedItem is not ModelProfile model ||
            templateAnalysis is null ||
            templateRepairPreview?.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired) ||
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
        logger.Info($"LM Studio instruction hierarchy: model={current.Id}, compatible={result.IsCompatible}, code={result.FailureCode ?? "none"}, control={Status(result.Control)}, leading={Status(result.LeadingDeveloper)}, conversation={Status(result.ConversationControl)}, continuation={Status(result.ContinuationDeveloper)}");
        MessageBox.Show(
            form,
            result.IsCompatible
                ? "Basic Control、Leading Developer、Conversation Control 与 Continuation Developer 四阶段均已通过。正式切换时仍会再次实时验证。"
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
        if (currentPreview.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired) || currentPreview.PatchedTemplate is null)
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
        bool templateUpgradeRequired = result.FailureCode == CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder;
        bool templateFixRequired = result.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
        form.LmStudio.HierarchyStatusValue.Text = result.IsCompatible
            ? "PASS"
            : templateUpgradeRequired
                ? "Template Upgrade Required (v2 → v3)"
                : templateFixRequired ? "Template Fix Required" : "FAILED";
        form.LmStudio.HierarchyStatusValue.ForeColor = result.IsCompatible ? Color.DarkGreen : Color.Firebrick;
        DisplayProbeStep(form.LmStudio.BasicControlValue, result.Control);
        DisplayProbeStep(form.LmStudio.LeadingDeveloperValue, result.LeadingDeveloper);
        DisplayProbeStep(form.LmStudio.ConversationControlValue, result.ConversationControl);
        DisplayProbeStep(form.LmStudio.ContinuationDeveloperValue, result.ContinuationDeveloper);
        form.LmStudio.HierarchyDetailValue.Text =
            $"Basic Control {FormatProbeStatus(result.Control)}；Leading Developer {FormatProbeStatus(result.LeadingDeveloper)}；Conversation Control {FormatProbeStatus(result.ConversationControl)}；Continuation Developer {FormatProbeStatus(result.ContinuationDeveloper)}；Failure Code: {result.FailureCode ?? "none"}。{result.Detail}";
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
        EnsureNoPendingLmStudioRecovery();
        SwitchRequest request = await CreateRequestAsync();
        try
        {
            lastPlan = await services.Switches.CreatePlanAsync(request, lifetime.Token);
            form.Current.OverrideWarningValue.Text = SummarizeOverrides(lastPlan.SecondaryOverrides);
            ShowPlan(lastPlan, false);
        }
        catch (LmStudioCompatibilityException exception) when (CanRepairTemplate(request, exception.Result))
        {
            DisplayHierarchyProbe(exception.Result);
            (LmStudioInstanceController previewController, LmStudioTemplateRepairPlan repairPlan) = await CreateTemplateRepairPlanAsync(request, exception.Result);
            using (previewController)
            {
                form.LmStudio.RuntimeRepairStatusValue.Text = "Preview Ready — 未执行 unload/load";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
                using var dialog = new LmStudioTemplateRepairDialog(repairPlan, allowApply: false);
                dialog.ShowDialog(form);
            }
        }
    }

    private async Task SwitchAsync()
    {
        EnsureNoPendingLmStudioRecovery();
        SwitchRequest request = await CreateRequestAsync();
        LmStudioInstanceController? instanceController = null;
        LmStudioTemplateRepairResult? repairResult = null;
        bool codexConfigCommitted = false;
        bool lifecycleStarted = false;
        try
        {
            SwitchPlan plan;
            try
            {
                plan = await services.Switches.CreatePlanAsync(request, lifetime.Token);
            }
            catch (LmStudioCompatibilityException exception) when (CanRepairTemplate(request, exception.Result))
            {
                DisplayHierarchyProbe(exception.Result);
                (instanceController, LmStudioTemplateRepairPlan repairPlan) = await CreateTemplateRepairPlanAsync(request, exception.Result);
                using var dialog = new LmStudioTemplateRepairDialog(repairPlan, allowApply: true);
                if (dialog.ShowDialog(form) != DialogResult.OK)
                {
                    form.LmStudio.RuntimeRepairStatusValue.Text = "Cancelled — 未执行 unload/load";
                    form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
                    return;
                }

                form.LmStudio.RuntimeRepairStatusValue.Text = "Applying — 正在卸载/加载/验证";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
                lifecycleStarted = true;
                SetLmStudioLifecycleBusy(true);
                logger.Info($"LM Studio runtime template repair confirmed: transaction={repairPlan.TransactionId:N}, instance={repairPlan.OriginalInstance.InstanceId}");
                repairResult = await instanceController.ApplyTemplateAsync(repairPlan, lifetime.Token);
                try
                {
                    form.LmStudio.RuntimeRepairStatusValue.Text = $"PatchedAndVerified — {repairResult.PatchedInstance.InstanceId}";
                    form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkGreen;
                    int? requestedCompact = request.AutoCompactTokenLimit;
                    await RefreshAndSelectLmStudioInstanceAsync(repairResult.PatchedInstance.InstanceId, requestedCompact);
                    DisplayHierarchyProbe(repairResult.HierarchyProbe);

                    request = await CreateRequestAsync();
                    if (!request.TargetModel.Equals(repairResult.PatchedInstance.InstanceId, StringComparison.Ordinal))
                    {
                        throw new IOException("LM Studio 新实例 ID 未正确进入切换请求。");
                    }

                    plan = await services.Switches.CreatePlanAsync(request, lifetime.Token);
                }
                catch (Exception continuationException)
                {
                    try
                    {
                        await RollbackAppliedRepairAsync(instanceController, repairResult, "补丁通过后刷新或重新生成 Codex 配置计划失败");
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException("补丁后续处理失败，且 LM Studio 事务回滚也失败。", continuationException, rollbackException);
                    }

                    throw;
                }
            }

            lastPlan = plan;
            if (ShowPlan(plan, true) != DialogResult.Yes)
            {
                if (repairResult is not null && instanceController is not null)
                {
                    await RollbackAppliedRepairAsync(instanceController, repairResult, "用户取消最终 Codex 配置确认");
                    string restoredTemplate = repairResult.Plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule
                        ? $"原 LM Studio 运行时模板 {repairResult.Plan.OriginalRuntimeTemplate.RuleVersion}"
                        : "原始 LM Studio 内置模板";
                    MessageBox.Show(form, $"已取消 Codex 配置切换，并恢复{restoredTemplate}实例。", "切换已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return;
            }

            try
            {
                SwitchRequest confirmedRequest = await CreateRequestAsync();
                if (confirmedRequest != plan.Request) throw new IOException("Provider/模型/context 在确认期间发生变化，请刷新并重新预览。");
                await services.Switches.CommitAsync(plan, lifetime.Token);
                codexConfigCommitted = true;
            }
            catch (Exception commitException)
            {
                if (!codexConfigCommitted && repairResult is not null && instanceController is not null)
                {
                    CodexEnvironmentInfo? authoritative = null;
                    try
                    {
                        authoritative = await services.RuntimeProbe.DetectAsync(CancellationToken.None);
                    }
                    catch (Exception auditException)
                    {
                        PreservePatchedInstanceForRecovery(repairResult, "Codex 配置提交报错，且无法重新读取权威配置；为避免卸载仍被配置引用的实例，已保留补丁实例。");
                        codexConfigCommitted = true;
                        throw new AggregateException("Codex 配置提交失败后无法确认磁盘最终状态；补丁实例已保留并禁止继续切换。", commitException, auditException);
                    }

                    bool pointsToPatchedInstance =
                        authoritative.Warning is null &&
                        authoritative.CurrentProvider == ProviderKind.LmStudio &&
                        string.Equals(authoritative.CurrentModel, repairResult.PatchedInstance.InstanceId, StringComparison.Ordinal);
                    if (pointsToPatchedInstance || authoritative.Warning is not null)
                    {
                        PreservePatchedInstanceForRecovery(
                            repairResult,
                            pointsToPatchedInstance
                                ? "Commit 返回错误，但磁盘配置已经指向补丁实例；已保留实例并等待恢复复验。"
                                : "Commit 返回错误，且配置重读存在警告；无法安全证明可回滚 LM Studio，已保留补丁实例。");
                        codexConfigCommitted = true;
                        throw new InvalidOperationException("Codex 配置提交结果不确定或已经指向补丁实例；未回滚 LM Studio，以避免配置引用被卸载的实例。", commitException);
                    }

                    try
                    {
                        await RollbackAppliedRepairAsync(instanceController, repairResult, "Codex 配置提交失败且磁盘配置确认未指向补丁实例");
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException("Codex 配置提交失败，且 LM Studio 事务回滚也失败。", commitException, rollbackException);
                    }
                }

                throw;
            }

            if (repairResult is not null && instanceController is not null)
            {
                try
                {
                    await instanceController.CompleteAsync(repairResult.Plan.TransactionId, lifetime.Token);
                    form.LmStudio.RuntimeRepairStatusValue.Text = $"Completed — 保留补丁实例 {repairResult.PatchedInstance.InstanceId}";
                    form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkGreen;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    lmStudioRecoveryPending = true;
                    ApplyLmStudioRecoveryGate();
                    form.LmStudio.RuntimeRepairStatusValue.Text = "Config Committed / Journal Pending — 必须保留补丁实例";
                    form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
                    logger.LogError("Codex 配置已提交，但 LM Studio 事务完成标记写入失败", exception);
                    MessageBox.Show(
                        form,
                        "Codex 配置已经成功切换，补丁实例必须保留；但恢复事务的 Completed 标记写入失败。请不要卸载当前补丁实例，也不要在下次启动时选择恢复原始模板，直到事务目录问题已修复。\n\n" + services.Redactor.Redact(exception.Message),
                        "切换已提交，但事务日志待处理",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            appSettings = await services.SettingsRepository.LoadAsync(lifetime.Token);
            logger.Info($"切换完成: provider={plan.Request.TargetProvider}, model={plan.Request.TargetModel}, files={plan.Files.Count}");
            MessageBox.Show(form, "切换完成，请重新启动 Codex。", "Codex Multi-Model Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshEnvironmentAsync();
            await RefreshHistoryAsync();
        }
        catch (Exception continuationException)
        {
            if (!codexConfigCommitted && repairResult is not null && instanceController is not null)
            {
                LmStudioTemplateTransactionRecord? record;
                try
                {
                    record = await services.TemplateTransactions.ReadAsync(repairResult.Plan.TransactionId, CancellationToken.None);
                }
                catch (Exception journalException) when (journalException is IOException or UnauthorizedAccessException or JsonException)
                {
                    lmStudioRecoveryPending = true;
                    ApplyLmStudioRecoveryGate();
                    throw new AggregateException("补丁成功后的处理失败，且恢复事务记录无法读取；已禁止新的切换。", continuationException, journalException);
                }

                if (record is null)
                {
                    lmStudioRecoveryPending = true;
                    ApplyLmStudioRecoveryGate();
                    throw new AggregateException(
                        "补丁成功后的处理失败，且恢复事务记录已丢失；已保留当前实例并禁止新的切换。",
                        continuationException,
                        new FileNotFoundException("LM Studio 恢复事务记录不存在。", services.TemplateTransactions.GetPath(repairResult.Plan.TransactionId)));
                }

                if (record.State is LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified)
                {
                    try
                    {
                        await RollbackAppliedRepairAsync(instanceController, repairResult, "补丁成功后的未处理异常");
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException("补丁成功后的处理失败，且 LM Studio 事务回滚也失败。", continuationException, rollbackException);
                    }
                }
            }

            throw;
        }
        finally
        {
            instanceController?.Dispose();
            if (lifecycleStarted)
            {
                SetLmStudioLifecycleBusy(false);
            }
        }
    }

    private void PreservePatchedInstanceForRecovery(LmStudioTemplateRepairResult repairResult, string detail)
    {
        lmStudioRecoveryPending = true;
        ApplyLmStudioRecoveryGate();
        form.LmStudio.RuntimeRepairStatusValue.Text = "Config State Uncertain — 保留补丁实例并禁止新切换";
        form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
        logger.Warning($"LM Studio patch preserved for recovery: transaction={repairResult.Plan.TransactionId:N}, instance={repairResult.PatchedInstance.InstanceId}, detail={detail}");
    }

    private async Task<(LmStudioInstanceController Controller, LmStudioTemplateRepairPlan Plan)> CreateTemplateRepairPlanAsync(
        SwitchRequest request,
        CodexInstructionHierarchyProbeResult failure)
    {
        if (request.LmStudioEndpoint is null || string.IsNullOrWhiteSpace(failure.FailureCode))
        {
            throw new InvalidOperationException("LM Studio 模板修复缺少 endpoint 或失败码。");
        }

        ModelProfile selected = lmModels.FirstOrDefault(model => model.Id.Equals(request.TargetModel, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("所选 LM Studio loaded instance 已不在当前 native 模型快照中，请刷新。");
        LmStudioInstanceController controller = services.CreateLmStudioInstanceController(request.LmStudioEndpoint, request.LmStudioRequiresAuthentication);
        controller.ProgressChanged += OnLmStudioLifecycleProgress;
        var planner = new LmStudioTemplateRepairPlanner(controller, services.GgufReader, services.TemplateRepair, services.TemplateTransactions);
        form.LmStudio.RuntimeRepairStatusValue.Text = "Planning — 捕获实例并分析精确 GGUF";
        form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
        LmStudioTemplateRepairPlan plan = await planner.CreatePlanAsync(selected, failure, lifetime.Token);
        logger.Info($"LM Studio runtime template repair preview: transaction={plan.TransactionId:N}, instance={plan.OriginalInstance.InstanceId}, variant={plan.OriginalInstance.SelectedVariant ?? plan.OriginalInstance.SourceModelKey}, originalSha={ShortHash(plan.GgufAnalysis.TemplateSha256)}, patchedSha={ShortHash(plan.TemplatePreview.PatchedTemplateSha256!)}");
        return (controller, plan);
    }

    private void OnLmStudioLifecycleProgress(object? sender, string stage)
    {
        if (form.IsDisposed || form.Disposing || !form.IsHandleCreated)
        {
            return;
        }

        void Update()
        {
            form.LmStudio.RuntimeRepairStatusValue.Text = stage;
            form.LmStudio.RuntimeRepairStatusValue.ForeColor =
                stage.Contains("Failed", StringComparison.OrdinalIgnoreCase) ? Color.Firebrick :
                stage.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                stage.Contains("RolledBack", StringComparison.OrdinalIgnoreCase) ||
                stage.Contains("PatchedAndVerified", StringComparison.OrdinalIgnoreCase) ? Color.DarkGreen :
                Color.DarkOrange;
        }

        if (form.InvokeRequired)
        {
            form.BeginInvoke((Action)Update);
        }
        else
        {
            Update();
        }
    }

    private async Task RefreshAndSelectLmStudioInstanceAsync(string instanceId, int? requestedCompact)
    {
        await RefreshLmStudioAsync();
        ModelProfile selected = lmModels.FirstOrDefault(model => model.IsLoaded == true && model.Id.Equals(instanceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"LM Studio load 响应实例 {instanceId} 未出现在 native 模型列表中。");
        updating = true;
        try
        {
            form.LmStudio.ModelCombo.SelectedItem = form.LmStudio.ModelCombo.Items.Cast<object>().OfType<ModelProfile>().First(model => model.Id.Equals(instanceId, StringComparison.Ordinal));
            if (form.Current.ProviderCombo.SelectedItem is ProviderKind.LmStudio)
            {
                form.Current.ModelCombo.SelectedItem = form.Current.ModelCombo.Items.Cast<object>().OfType<ModelProfile>().First(model => model.Id.Equals(instanceId, StringComparison.Ordinal));
            }
        }
        finally
        {
            updating = false;
        }

        UpdateLocalModelDetails();
        UpdateReasoningChoices();
        if (requestedCompact is > 0 && selected.LoadedContextLength is int context && requestedCompact < context && context - requestedCompact >= 1024)
        {
            form.LmStudio.AutoCompactInput.Value = Math.Clamp(requestedCompact.Value, (int)form.LmStudio.AutoCompactInput.Minimum, (int)form.LmStudio.AutoCompactInput.Maximum);
        }
    }

    private async Task RollbackAppliedRepairAsync(
        LmStudioInstanceController controller,
        LmStudioTemplateRepairResult repair,
        string reason)
    {
        form.LmStudio.RuntimeRepairStatusValue.Text = "Rolling Back — " + reason;
        form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
        logger.Warning($"LM Studio runtime template rollback requested: transaction={repair.Plan.TransactionId:N}, reason={reason}");
        LmStudioRollbackResult rollback = await controller.RollbackAsync(repair.Plan, repair.PatchedInstance.InstanceId, lifetime.Token);
        if (!rollback.Succeeded)
        {
            lmStudioRecoveryPending = true;
            ApplyLmStudioRecoveryGate();
            form.LmStudio.RuntimeRepairStatusValue.Text = "RollbackFailed — " + rollback.Detail;
            form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
            throw new InvalidOperationException(rollback.Detail);
        }

        form.LmStudio.RuntimeRepairStatusValue.Text = repair.Plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule
            ? $"RolledBack — 已恢复 {repair.Plan.OriginalRuntimeTemplate.RuleVersion}"
            : "RolledBack — 已恢复原始内置模板";
        form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkGreen;
        await RefreshLmStudioAsync();
    }

    private static bool CanRepairTemplate(SwitchRequest request, CodexInstructionHierarchyProbeResult result) =>
        request.TargetProvider == ProviderKind.LmStudio &&
        result.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or
            CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder;

    private async Task RecoverIncompleteLmStudioTransactionsAsync(bool showNoPendingMessage = false)
    {
        IReadOnlyList<LmStudioTemplateTransactionRecord> incomplete;
        try
        {
            incomplete = await services.TemplateTransactions.ListIncompleteAsync(lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            lmStudioRecoveryPending = true;
            ApplyLmStudioRecoveryGate();
            form.LmStudio.RuntimeRepairStatusValue.Text = "Recovery Journal Invalid — 禁止新切换";
            form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
            throw new InvalidDataException("LM Studio 恢复事务目录包含无法验证的记录；为避免覆盖未知运行状态，已禁止新的 Preview/Switch。", exception);
        }

        lmStudioRecoveryPending = incomplete.Count > 0;
        if (incomplete.Count == 0)
        {
            ApplyLmStudioRecoveryGate();
            if (showNoPendingMessage)
            {
                MessageBox.Show(form, "当前没有未完成的 LM Studio 模板事务。", "LM Studio 恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        if (incomplete.Count > 1)
        {
            ApplyLmStudioRecoveryGate();
            form.LmStudio.RuntimeRepairStatusValue.Text = $"Recovery Ambiguous — 检测到 {incomplete.Count} 个未完成事务";
            form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
            MessageBox.Show(
                form,
                $"事务目录中同时存在 {incomplete.Count} 个未完成的 LM Studio 生命周期事务。为避免按错误顺序卸载或加载实例，本次不会自动恢复；新的 Preview/Switch 已禁止。请先审查 transactions 目录中的记录。",
                "LM Studio 恢复状态有歧义",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        foreach (LmStudioTemplateTransactionRecord transaction in incomplete)
        {
            try
            {
                if (await TryFinalizeAlreadyCommittedLmStudioTransactionAsync(transaction))
                {
                    continue;
                }
            }
            catch (Exception exception)
            {
                lmStudioRecoveryPending = true;
                ApplyLmStudioRecoveryGate();
                form.LmStudio.RuntimeRepairStatusValue.Text = "Committed Transaction Verification Failed — 禁止恢复/切换";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
                logger.LogError($"LM Studio committed transaction verification failed: id={transaction.TransactionId:N}", exception);
                MessageBox.Show(form, services.Redactor.Redact("Codex 配置似乎已指向补丁实例，但无法验证并完成事务。为避免恢复原模板后让 Codex 指向错误实例，本次没有执行 unload/load。\n\n" + exception.Message), "LM Studio 已提交事务待处理", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            LmStudioRecoveryAssessment assessment;
            bool requiresAuthentication;
            try
            {
                requiresAuthentication = await DetectLmStudioAuthenticationAsync(transaction.OriginalInstance.Endpoint);
                using LmStudioInstanceController assessmentController = services.CreateLmStudioInstanceController(transaction.OriginalInstance.Endpoint, requiresAuthentication);
                form.LmStudio.RuntimeRepairStatusValue.Text = $"Assessing Recovery — {transaction.TransactionId:N}";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
                assessment = await assessmentController.AssessRecoveryAsync(transaction, lifetime.Token);
            }
            catch (Exception exception)
            {
                lmStudioRecoveryPending = true;
                ApplyLmStudioRecoveryGate();
                form.LmStudio.RuntimeRepairStatusValue.Text = "Recovery Assessment Failed — 禁止新切换";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
                logger.LogError($"LM Studio recovery assessment failed: id={transaction.TransactionId:N}", exception);
                MessageBox.Show(form, services.Redactor.Redact(exception.Message), "LM Studio 恢复评估失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (assessment.Disposition == LmStudioRecoveryDisposition.BlockedAmbiguous)
            {
                form.LmStudio.RuntimeRepairStatusValue.Text = "Recovery Ambiguous — 未执行 unload/load";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
                ApplyLmStudioRecoveryGate();
                MessageBox.Show(form, BuildRecoveryAssessmentMessage(transaction, assessment), "LM Studio 恢复状态有歧义", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (MessageBox.Show(form, BuildRecoveryAssessmentMessage(transaction, assessment), "LM Studio 未完成事务", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                form.LmStudio.RuntimeRepairStatusValue.Text = $"Recovery Required — {transaction.TransactionId:N}";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
                ApplyLmStudioRecoveryGate();
                return;
            }

            try
            {
                using LmStudioInstanceController controller = services.CreateLmStudioInstanceController(transaction.OriginalInstance.Endpoint, requiresAuthentication);
                form.LmStudio.RuntimeRepairStatusValue.Text = $"Recovering — {transaction.TransactionId:N}";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkOrange;
                SetLmStudioLifecycleBusy(true);
                LmStudioRollbackResult result = await controller.RecoverAsync(transaction, assessment, lifetime.Token);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(result.Detail);
                }

                logger.Info($"LM Studio incomplete transaction recovered: id={transaction.TransactionId:N}, detail={result.Detail}");
                form.LmStudio.RuntimeRepairStatusValue.Text = transaction.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.ManagerRule
                    ? $"Recovered — 已验证 {transaction.OriginalRuntimeRuleVersion} 实例状态"
                    : "Recovered — 已验证原始内置模板实例状态";
                form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkGreen;
            }
            catch (Exception exception)
            {
                lmStudioRecoveryPending = true;
                ApplyLmStudioRecoveryGate();
                logger.LogError($"LM Studio incomplete transaction recovery failed: id={transaction.TransactionId:N}", exception);
                MessageBox.Show(form, services.Redactor.Redact(exception.Message), "LM Studio 恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                SetLmStudioLifecycleBusy(false);
            }
        }

        lmStudioRecoveryPending = (await services.TemplateTransactions.ListIncompleteAsync(lifetime.Token)).Count > 0;
        ApplyLmStudioRecoveryGate();
        if (!lmStudioRecoveryPending && incomplete.Count > 0)
        {
            await RefreshEnvironmentAsync();
            await RefreshLmStudioAsync();
        }
    }

    private static string BuildRecoveryAssessmentMessage(
        LmStudioTemplateTransactionRecord transaction,
        LmStudioRecoveryAssessment assessment)
    {
        var builder = new StringBuilder()
            .AppendLine("检测到未完成的 LM Studio 模板事务。")
            .AppendLine()
            .Append("Transaction: ").AppendLine(transaction.TransactionId.ToString("N"))
            .Append("Journal schema/state: ").Append(transaction.SchemaVersion).Append(" / ").AppendLine(transaction.State.ToString())
            .Append("Last stable/failure stage: ").Append(transaction.LastStableState?.ToString() ?? "<legacy unknown>").Append(" / ").AppendLine(transaction.FailureStage.ToString())
            .Append("Endpoint: ").AppendLine(transaction.OriginalInstance.Endpoint.GetLeftPart(UriPartial.Authority))
            .Append("REST load key: ").AppendLine(transaction.OriginalInstance.SourceModelKey)
            .Append("Expected variant: ").AppendLine(transaction.OriginalInstance.SelectedVariant ?? "<unknown>")
            .Append("Original instance: ").AppendLine(transaction.OriginalInstance.InstanceId)
            .Append("Known patched/candidate: ").AppendLine(transaction.PatchedInstanceId ?? transaction.CandidateInstanceId ?? "<unknown>")
            .Append("Context: ").AppendLine(transaction.OriginalInstance.LoadConfiguration.ContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "unknown")
            .Append("Original runtime template: ").Append(transaction.OriginalRuntimeTemplateMode)
            .Append(transaction.OriginalRuntimeRuleVersion is null ? string.Empty : " / " + transaction.OriginalRuntimeRuleVersion).AppendLine()
            .Append("Target runtime rule: ").AppendLine(transaction.TargetRuntimeRuleVersion ?? transaction.RuleVersion)
            .AppendLine()
            .AppendLine("当前权威 native 候选：");
        if (assessment.Candidates.Count == 0)
        {
            builder.AppendLine("- <none>");
        }
        else
        {
            foreach (LmStudioRecoveryCandidate candidate in assessment.Candidates)
            {
                builder.Append("- ").Append(candidate.Snapshot.InstanceId)
                    .Append(" | config=").Append(candidate.MatchesOriginalSnapshot ? "MATCH" : "MISMATCH")
                    .Append(" | basic=").Append(candidate.HierarchyProbe is null ? "not-run" : Status(candidate.HierarchyProbe.Control))
                    .Append(" | leading=").Append(candidate.HierarchyProbe is null ? "not-run" : Status(candidate.HierarchyProbe.LeadingDeveloper))
                    .Append(" | conversation=").Append(candidate.HierarchyProbe is null ? "not-run" : Status(candidate.HierarchyProbe.ConversationControl))
                    .Append(" | continuation=").Append(candidate.HierarchyProbe is null ? "not-run" : Status(candidate.HierarchyProbe.ContinuationDeveloper))
                    .Append(" | failure=").AppendLine(candidate.HierarchyProbe?.FailureCode ?? "none");
            }
        }

        builder.AppendLine()
            .Append("评估结果: ").AppendLine(assessment.Disposition.ToString())
            .AppendLine(assessment.Detail)
            .AppendLine()
            .AppendLine(assessment.RequiresLifecycleMutation
                ? "确认后将执行上述精确 unload/load；执行前会再次指纹复查。"
                : "确认后只会把 journal 标记为 RolledBack，不会 unload/load 模型。")
            .AppendLine("恢复完成前新的 Preview/Switch 保持禁用。是否继续？");
        return builder.ToString();
    }

    private async Task<bool> TryFinalizeAlreadyCommittedLmStudioTransactionAsync(LmStudioTemplateTransactionRecord transaction)
    {
        if (transaction.State != LmStudioTemplateTransactionState.PatchedAndVerified || string.IsNullOrWhiteSpace(transaction.PatchedInstanceId))
        {
            return false;
        }

        CodexEnvironmentInfo environment = await services.RuntimeProbe.DetectAsync(lifetime.Token);
        if (environment.CurrentProvider != ProviderKind.LmStudio || !string.Equals(environment.CurrentModel, transaction.PatchedInstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        string message =
            "恢复记录停留在 PatchedAndVerified，但当前 Codex 配置已经指向同一补丁实例。这通常表示配置提交成功后、写入 Completed 标记前程序退出。\n\n" +
            $"Transaction: {transaction.TransactionId:N}\n" +
            $"Patched instance: {transaction.PatchedInstanceId}\n" +
            $"Codex context: {transaction.OriginalInstance.LoadConfiguration.ContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "unknown"}\n\n" +
            "是否只重新验证实例与指令层级并补写 Completed 标记？此操作不会 unload/load 模型，也不会修改 Codex 配置。";
        if (MessageBox.Show(form, message, "完成已提交的 LM Studio 事务", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            form.LmStudio.RuntimeRepairStatusValue.Text = $"Completion Required — {transaction.TransactionId:N}";
            form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.Firebrick;
            return true;
        }

        if (environment.IsRunning)
        {
            throw new InvalidOperationException("请先完全关闭 Codex，再验证并完成已提交事务。");
        }

        bool requiresAuthentication = await DetectLmStudioAuthenticationAsync(transaction.OriginalInstance.Endpoint);
        using LmStudioInstanceController controller = services.CreateLmStudioInstanceController(transaction.OriginalInstance.Endpoint, requiresAuthentication);
        LmStudioLoadedInstanceSnapshot patched = await controller.CaptureAsync(transaction.PatchedInstanceId, lifetime.Token);
        if (!string.Equals(patched.SourceModelKey, transaction.OriginalInstance.SourceModelKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(patched.SelectedVariant, transaction.OriginalInstance.SelectedVariant, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(patched.Architecture, transaction.OriginalInstance.Architecture, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(patched.Quantization, transaction.OriginalInstance.Quantization, StringComparison.OrdinalIgnoreCase) ||
            !LmStudioClient.LoadConfigurationsEqual(transaction.OriginalInstance.LoadConfiguration, patched.LoadConfiguration))
        {
            throw new InvalidDataException("当前补丁实例的模型变体或加载配置与事务记录不一致。");
        }

        var request = new SwitchRequest(
            ProviderKind.LmStudio,
            patched.InstanceId,
            ContextWindow: patched.LoadConfiguration.ContextLength,
            LmStudioEndpoint: patched.Endpoint,
            LmStudioRequiresAuthentication: requiresAuthentication,
            TargetModelType: patched.ModelType);
        CodexInstructionHierarchyProbeResult hierarchy = await services.LmStudioPreflight.ProbeAsync(request, lifetime.Token);
        if (!hierarchy.IsCompatible)
        {
            throw new LmStudioCompatibilityException(hierarchy);
        }

        await controller.CompleteAsync(transaction.TransactionId, lifetime.Token);
        form.LmStudio.RuntimeRepairStatusValue.Text = $"Completed — 保留补丁实例 {patched.InstanceId}";
        form.LmStudio.RuntimeRepairStatusValue.ForeColor = Color.DarkGreen;
        logger.Info($"LM Studio committed transaction finalized after restart: id={transaction.TransactionId:N}, instance={patched.InstanceId}");
        return true;
    }

    private async Task<bool> DetectLmStudioAuthenticationAsync(Uri endpoint)
    {
        var unauthenticated = new LmStudioClient(endpoint, null, httpClient);
        ProviderProbeResult probe = await unauthenticated.ProbeAsync(lifetime.Token);
        if (probe.RequiresAuthentication)
        {
            if (string.IsNullOrWhiteSpace(GetSecret(CredentialNames.LmStudio)))
            {
                throw new InvalidOperationException("LM Studio 需要 Token，但 Windows Credential Manager 中没有可用凭据。");
            }

            return true;
        }

        if (!probe.IsAvailable)
        {
            throw new InvalidOperationException("无法连接事务记录中的 LM Studio endpoint: " + probe.Summary);
        }

        return false;
    }

    private void EnsureNoPendingLmStudioRecovery()
    {
        if (lmStudioLifecycleBusy)
        {
            throw new InvalidOperationException("LM Studio 实例生命周期操作正在进行；请等待当前卸载、加载或恢复完成。");
        }

        if (lmStudioRecoveryPending)
        {
            throw new InvalidOperationException("存在未完成或回滚失败的 LM Studio 模板事务；请在 LM Studio 页使用“检查/恢复未完成事务”完成评估与恢复。");
        }
    }

    private void SetLmStudioLifecycleBusy(bool busy)
    {
        Control[] lifecycleControls =
        [
            form.Current.RefreshButton,
            form.Current.PreviewButton,
            form.Current.SwitchButton,
            form.Current.ProviderCombo,
            form.Current.ModelCombo,
            form.Current.ReasoningCombo,
            form.Current.SecondaryPolicyCombo,
            form.Current.SecondaryOverridesList,
            form.LmStudio.DetectButton,
            form.LmStudio.RefreshModelsButton,
            form.LmStudio.RecoverTransactionButton,
            form.LmStudio.EndpointText,
            form.LmStudio.ModelCombo,
            form.LmStudio.CodexContextInput,
            form.LmStudio.AutoCompactInput,
            form.LmStudio.GgufPathText,
            form.LmStudio.BrowseGgufButton,
            form.LmStudio.AnalyzeTemplateButton,
            form.LmStudio.ExportTemplateButton,
            form.LmStudio.CopyTemplateButton,
            form.LmStudio.RecheckHierarchyButton,
        ];

        if (busy)
        {
            if (lmStudioLifecycleBusy)
            {
                return;
            }

            lmStudioLifecycleControlStates.Clear();
            foreach (Control control in lifecycleControls)
            {
                lmStudioLifecycleControlStates[control] = control.Enabled;
                control.Enabled = false;
            }

            lmStudioLifecycleBusy = true;
        }
        else
        {
            if (!lmStudioLifecycleBusy)
            {
                ApplyLmStudioRecoveryGate();
                return;
            }

            lmStudioLifecycleBusy = false;
            foreach ((Control control, bool enabled) in lmStudioLifecycleControlStates)
            {
                control.Enabled = enabled;
            }

            lmStudioLifecycleControlStates.Clear();
        }

        ApplyLmStudioRecoveryGate();
    }

    private void ApplyLmStudioRecoveryGate()
    {
        form.LmStudio.RecoverTransactionButton.Enabled = lmStudioRecoveryPending && !lmStudioLifecycleBusy;

        if (lmStudioRecoveryPending || lmStudioLifecycleBusy)
        {
            form.Current.PreviewButton.Enabled = false;
            form.Current.SwitchButton.Enabled = false;
        }
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
        CompatibilityResult[] resultArray = results.ToArray();
        CompatibilityResult? hierarchy = resultArray.FirstOrDefault(item => item.Capability == "Codex Instruction Hierarchy");
        if (hierarchy is null)
        {
            return;
        }

        bool templateUpgradeRequired = hierarchy.FailureCode == CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder;
        bool templateFixRequired = hierarchy.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
        form.LmStudio.HierarchyStatusValue.Text = hierarchy.Status == CompatibilityStatus.Supported
            ? "PASS"
            : templateUpgradeRequired
                ? "Template Upgrade Required (v2 → v3)"
                : templateFixRequired ? "Template Fix Required" : "FAILED";
        form.LmStudio.HierarchyStatusValue.ForeColor = hierarchy.Status == CompatibilityStatus.Supported ? Color.DarkGreen : Color.Firebrick;
        DisplayCompatibilityProbeStep(form.LmStudio.BasicControlValue, resultArray, "Basic Control");
        DisplayCompatibilityProbeStep(form.LmStudio.LeadingDeveloperValue, resultArray, "Leading Developer");
        DisplayCompatibilityProbeStep(form.LmStudio.ConversationControlValue, resultArray, "Conversation Control");
        DisplayCompatibilityProbeStep(form.LmStudio.ContinuationDeveloperValue, resultArray, "Continuation Developer");
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
                .Append("  Basic Control: ").AppendLine(FormatProbeStatus(preflight.Control))
                .Append("  Leading Developer: ").AppendLine(FormatProbeStatus(preflight.LeadingDeveloper))
                .Append("  Conversation Control: ").AppendLine(FormatProbeStatus(preflight.ConversationControl))
                .Append("  Continuation Developer: ").AppendLine(FormatProbeStatus(preflight.ContinuationDeveloper));
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
        catch (LmStudioTemplateApplyException exception)
        {
            logger.LogError($"LM Studio runtime template repair failed; rollbackSucceeded={exception.Rollback.Succeeded}, transaction={Path.GetFileNameWithoutExtension(exception.Rollback.TransactionPath)}", exception.InnerException ?? exception);
            form.LmStudio.RuntimeRepairStatusValue.Text = exception.Rollback.Succeeded ? "RolledBack — 修复失败，原实例已恢复" : "RollbackFailed — 需要恢复";
            form.LmStudio.RuntimeRepairStatusValue.ForeColor = exception.Rollback.Succeeded ? Color.DarkGreen : Color.Firebrick;
            if (!exception.Rollback.Succeeded)
            {
                lmStudioRecoveryPending = true;
                ApplyLmStudioRecoveryGate();
            }

            var detail = new StringBuilder()
                .AppendLine("LM Studio 运行时 Prompt Template 修复失败。")
                .Append("请求阶段: ").AppendLine(exception.FailureStage.ToString())
                .Append("REST load key: ").AppendLine(exception.Plan.OriginalInstance.SourceModelKey)
                .Append("Expected variant: ").AppendLine(exception.Plan.OriginalInstance.SelectedVariant ?? "<unknown>");
            if (exception.InnerException is LmStudioApiException apiException)
            {
                detail.AppendLine(FormatLmStudioApiFailure(apiException.Failure));
            }
            else if (exception.InnerException is not null)
            {
                detail.Append("原始错误: ").Append(exception.InnerException.GetType().Name).Append(": ").AppendLine(exception.InnerException.Message);
            }

            detail.AppendLine()
                .AppendLine(exception.Rollback.Detail)
                .Append("事务记录：").Append(exception.Rollback.TransactionPath);
            MessageBox.Show(form, services.Redactor.Redact(detail.ToString()), "LM Studio 运行时模板修复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (LmStudioCompatibilityException exception)
        {
            DisplayHierarchyProbe(exception.Result);
            logger.Warning($"LM Studio compatibility blocked: code={exception.Result.FailureCode ?? CompatibilityFailureCodes.OtherProviderError}, control={Status(exception.Result.Control)}, leading={Status(exception.Result.LeadingDeveloper)}, conversation={Status(exception.Result.ConversationControl)}, continuation={Status(exception.Result.ContinuationDeveloper)}");
            MessageBox.Show(form, services.Redactor.Redact(exception.Message), "LM Studio Prompt Template 不兼容", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (LmStudioApiException exception)
        {
            logger.LogError($"LM Studio API request failed: status={exception.Failure.HttpStatus}, type={exception.Failure.ErrorType ?? "none"}, code={exception.Failure.ErrorCode ?? "none"}", exception);
            ModelProfile? selected = form.Current.ModelCombo.SelectedItem as ModelProfile;
            string context = selected?.Provider == ProviderKind.LmStudio
                ? $"请求阶段: preflight/schema\nREST load key: {selected.SourceModelKey ?? "<unknown>"}\nExpected variant: {selected.SelectedVariant ?? "<unknown>"}\n"
                : string.Empty;
            MessageBox.Show(form, services.Redactor.Redact(context + FormatLmStudioApiFailure(exception.Failure)), "LM Studio HTTP 请求失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private static string FormatProbeStatus(CodexInstructionProbeStepResult result) =>
        $"{(result.Passed ? "PASS" : result.HttpStatus is null ? "NOT RUN" : "FAILED")} (HTTP {result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "未返回"})";

    private static string Status(CodexInstructionProbeStepResult result) =>
        $"{(result.Passed ? "PASS" : result.HttpStatus is null ? "NOT-RUN" : "FAIL")}/{result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}";

    private static void DisplayProbeStep(Label label, CodexInstructionProbeStepResult? result)
    {
        label.Text = result is null ? "NOT RUN" : FormatProbeStatus(result);
        label.ForeColor = result is null ? Color.DarkOrange : result.Passed ? Color.DarkGreen : result.HttpStatus is null ? Color.DarkOrange : Color.Firebrick;
    }

    private static void DisplayCompatibilityProbeStep(
        Label label,
        IReadOnlyList<CompatibilityResult> results,
        string capability)
    {
        CompatibilityResult? result = results.FirstOrDefault(item => item.Capability == capability);
        if (result is null)
        {
            DisplayProbeStep(label, null);
            return;
        }

        label.Text = $"{result.Status}: {result.Detail}";
        label.ForeColor = result.Status switch
        {
            CompatibilityStatus.Supported => Color.DarkGreen,
            CompatibilityStatus.Failed => Color.Firebrick,
            _ => Color.DarkOrange,
        };
    }

    private static string FormatLmStudioApiFailure(LmStudioApiFailure failure) =>
        $"HTTP: {failure.HttpStatus.ToString(CultureInfo.InvariantCulture)}\n" +
        $"error.type: {failure.ErrorType ?? "<none>"}\n" +
        $"error.code: {failure.ErrorCode ?? "<none>"}\n" +
        $"error.param: {failure.Parameter ?? "<none>"}\n" +
        $"error.message: {failure.Message}";

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
