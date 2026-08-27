using System.Globalization;
using System.Text;
using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

internal sealed class LmStudioTemplateRepairDialog : Form
{
    public LmStudioTemplateRepairDialog(LmStudioTemplateRepairPlan plan, bool allowApply)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Text = allowApply ? "确认 LM Studio Prompt Template 持久修复" : "LM Studio Prompt Template 持久修复预览";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 640);
        Size = new Size(1120, 800);
        ShowInTaskbar = false;

        var summary = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = BuildSummary(plan),
        };
        var original = TemplateText(plan.GgufAnalysis.ChatTemplate);
        var runtime = TemplateText(plan.OriginalRuntimeTemplateText ?? plan.GgufAnalysis.ChatTemplate);
        var patched = TemplateText(plan.TemplatePreview.PatchedTemplate ?? string.Empty);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("事务与加载配置", summary));
        tabs.TabPages.Add(Page("GGUF 原始模板（只读）", original));
        if (plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule)
        {
            tabs.TabPages.Add(Page($"当前运行时 {plan.OriginalRuntimeTemplate.RuleVersion}（只读）", runtime));
        }

        tabs.TabPages.Add(Page($"目标 {plan.TemplatePreview.RuleVersion}（只读）", patched));

        var warning = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 10, 12, 10),
            ForeColor = Color.DarkRed,
            Text = allowApply
                ? plan.PersistentDefaults is null
                    ? "该兼容计划没有持久 defaults 身份，自动应用已被阻断；请关闭并重新刷新模型。"
                    : $"GGUF 不会被修改。确认后会事务式修改该模型的 LM Studio 默认 Prompt Template（{plan.PersistentDefaults.Mutation}），然后执行一次 unload/reload；目标 /load 不发送 REST prompt_template。任何失败都会先恢复 defaults，再恢复原实例。"
                : "这是非变更 Preview：不会修改 GGUF、LM Studio defaults 或 Codex 配置，也不会 unload/load 模型。",
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        var primary = new Button
        {
            AutoSize = false,
            Width = allowApply ? 220 : 110,
            Height = 34,
            Text = allowApply ? "应用兼容模板并继续" : "关闭",
            DialogResult = DialogResult.OK,
            Enabled = !allowApply || plan.PersistentDefaults is not null,
        };
        buttons.Controls.Add(primary);
        AcceptButton = primary;
        if (allowApply)
        {
            var cancel = new Button { AutoSize = false, Width = 110, Height = 34, Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            CancelButton = cancel;
        }

        Controls.Add(tabs);
        Controls.Add(warning);
        Controls.Add(buttons);
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        page.Controls.Add(content);
        return page;
    }

    private static TextBox TemplateText(string text) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        Text = text,
    };

    private static string BuildSummary(LmStudioTemplateRepairPlan plan)
    {
        LmStudioLoadedInstanceSnapshot instance = plan.OriginalInstance;
        LmStudioLoadConfiguration config = instance.LoadConfiguration;
        var builder = new StringBuilder()
            .AppendLine("运行时补丁事务")
            .Append("Transaction ID: ").AppendLine(plan.TransactionId.ToString("N"))
            .Append("Failure Code: ").AppendLine(plan.FailureCode)
            .Append("Endpoint: ").AppendLine(instance.Endpoint.GetLeftPart(UriPartial.Authority))
            .Append("Source Model: ").AppendLine(instance.SourceModelKey)
            .Append("REST load key: ").AppendLine(instance.SourceModelKey)
            .Append("Expected selected variant: ").AppendLine(instance.SelectedVariant ?? "<none>")
            .Append("Available variants: ").AppendLine(instance.LoadTarget?.AvailableVariants.Count > 0 ? string.Join(", ", instance.LoadTarget.AvailableVariants) : "<not exposed>")
            .Append("Current Instance ID: ").AppendLine(instance.InstanceId)
            .AppendLine("Reloaded Instance ID: returned by LM Studio; it may differ from the current ID and is never predicted")
            .Append("Architecture / Quantization: ").Append(instance.Architecture ?? "unknown").Append(" / ").AppendLine(instance.Quantization ?? "unknown")
            .Append("Parameters / Max Context: ").Append(instance.Parameters ?? "unknown").Append(" / ").AppendLine(Value(instance.MaxContextLength))
            .Append("Captured at / fingerprint: ").Append(instance.CapturedAt.ToString("O", CultureInfo.InvariantCulture)).Append(" / ").AppendLine(instance.Fingerprint)
            .Append("GGUF: ").AppendLine(plan.ModelFile.FilePath)
            .Append("GGUF locator: ").AppendLine(plan.ModelFile.Source)
            .Append("GGUF version: ").AppendLine(plan.GgufAnalysis.GgufVersion.ToString(CultureInfo.InvariantCulture))
            .Append("GGUF bytes / modified UTC: ").Append(plan.GgufAnalysis.FileLength.ToString("N0", CultureInfo.CurrentCulture)).Append(" / ").AppendLine(plan.GgufAnalysis.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append("Original template SHA-256: ").AppendLine(plan.GgufAnalysis.TemplateSha256)
            .Append("Current runtime mode/rule: ").Append(plan.OriginalRuntimeTemplate.Mode).Append(" / ").AppendLine(plan.OriginalRuntimeTemplate.RuleVersion ?? "<built-in>")
            .Append("Current runtime template SHA-256: ").AppendLine(plan.OriginalRuntimeTemplate.TemplateSha256 ?? plan.GgufAnalysis.TemplateSha256)
            .Append("Runtime provenance transaction: ").AppendLine(plan.OriginalRuntimeTemplate.EvidenceTransactionId?.ToString("N") ?? "<not required>")
            .Append("Patched template SHA-256:  ").AppendLine(plan.TemplatePreview.PatchedTemplateSha256)
            .Append("Target runtime rule: ").AppendLine(plan.TemplatePreview.RuleVersion)
            .AppendLine()
            .AppendLine("LM Studio 持久 per-model defaults")
            .Append("LM Studio version: ").AppendLine(plan.LmStudioVersion ?? "<unsupported/unknown>")
            .Append("Concrete model identifier: ").AppendLine(plan.PersistentDefaults?.ConcreteModelIdentifier ?? "<not proven>")
            .Append("Defaults path: ").AppendLine(plan.PersistentDefaults?.FilePath ?? "<not available>")
            .Append("Mutation: ").AppendLine(plan.PersistentDefaults?.Mutation.ToString() ?? "<blocked>")
            .Append("Original defaults SHA-256: ").AppendLine(plan.PersistentDefaults?.OriginalFingerprint.Sha256 ?? "<not available>")
            .Append("Candidate defaults SHA-256: ").AppendLine(plan.PersistentDefaults?.CandidateFingerprint.Sha256 ?? "<not available>")
            .Append("Original persistent field: ").AppendLine(plan.PersistentDefaults?.OriginalFieldState.ToString() ?? "<not available>")
            .Append("Original persistent template SHA-256: ").AppendLine(plan.PersistentDefaults?.OriginalTemplateSha256 ?? "<missing>")
            .Append("Target persistent template SHA-256: ").AppendLine(plan.PersistentDefaults?.TargetTemplateSha256 ?? "<not available>")
            .AppendLine("Only llm.load.promptTemplate changes; preset、operation、其他 load 字段和未知 JSON 属性保持语义不变。")
            .AppendLine("GGUF remains read-only. Successful target /load omits top-level REST prompt_template so persistence is proven by per-model defaults.")
            .AppendLine()
            .AppendLine("将保留的 LM Studio 加载配置")
            .Append("context_length: ").AppendLine(Value(config.ContextLength))
            .Append("eval_batch_size: ").AppendLine(Value(config.EvalBatchSize))
            .Append("physical_batch_size: ").AppendLine(Value(config.PhysicalBatchSize))
            .Append("parallel: ").AppendLine(Value(config.Parallel))
            .Append("flash_attention: ").AppendLine(Value(config.FlashAttention))
            .Append("context_checkpoints: ").AppendLine(Value(config.ContextCheckpoints))
            .Append("reasoning_budget_message: ").AppendLine(config.ReasoningBudgetMessage is null ? "<omitted>" : JsonString(config.ReasoningBudgetMessage))
            .Append("speculative_draft_mtp: ").AppendLine(Value(config.SpeculativeDraftMtp))
            .Append("speculative_draft_simple: ").AppendLine(Value(config.SpeculativeDraftSimple))
            .Append("speculative_draft_model: ").AppendLine(config.SpeculativeDraftModel is null ? "<omitted>" : JsonString(config.SpeculativeDraftModel))
            .Append("speculative_draft_max_tokens: ").AppendLine(Value(config.SpeculativeDraftMaxTokens))
            .Append("speculative_draft_min_tokens: ").AppendLine(Value(config.SpeculativeDraftMinTokens))
            .Append("speculative_draft_min_continue_probability: ").AppendLine(Value(config.SpeculativeDraftMinContinueProbability))
            .Append("offload_kv_cache_to_gpu: ").AppendLine(Value(config.OffloadKvCacheToGpu))
            .Append("num_experts: ").AppendLine(Value(config.NumExperts))
            .Append("remaining TTL: ").AppendLine(instance.RemainingTtlSeconds is > 0 ? $"{instance.RemainingTtlSeconds} seconds（只能恢复捕获时剩余值）" : "<none>")
            .AppendLine()
            .AppendLine("回滚策略")
            .AppendLine("- Prepared 后先创建 CurrentUser DPAPI 加密备份并进行解密/SHA 回读；备份失败时不会写 defaults 或 unload。")
            .AppendLine("- load、配置一致性或四阶段探测任一步失败：先精确/字段级恢复 per-model Prompt Template，再恢复原实例及原四阶段签名。")
            .AppendLine("- 若 Prompt Template 字段被外部改成未知内容：进入 RecoveryBlocked，绝不覆盖用户模板，也不继续 unload/load。")
            .AppendLine("- 补丁通过后若取消最终 Codex 配置确认，或 Codex 配置提交失败：同样恢复原 defaults 与原实例。")
            .AppendLine("- Codex 配置提交成功：仅在持久字段、当前 instance 和四阶段再次验证后标记 Completed / PersistentDefaultVerified。")
            .AppendLine("- selected_variant 只用于量化/文件强校验；/load 的 model 固定使用 REST load key。")
            .AppendLine("- 新 instance ID 以后端响应为准，不预测 :2 后缀。")
            .AppendLine("- 卸载与重新加载可能耗时数分钟；期间请勿启动 Codex 或在其他窗口操作同一模型。");
        return builder.ToString();
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Value<T>(T? value) where T : struct => value?.ToString() ?? "<omitted>";
}
