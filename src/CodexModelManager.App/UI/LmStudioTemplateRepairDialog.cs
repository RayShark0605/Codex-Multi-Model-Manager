using System.Globalization;
using System.Text;
using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

internal sealed class LmStudioTemplateRepairDialog : Form
{
    public LmStudioTemplateRepairDialog(LmStudioTemplateRepairPlan plan, bool allowApply)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Text = allowApply ? "确认 LM Studio Prompt Template 运行时修复" : "LM Studio Prompt Template 运行时修复预览";
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
                ? plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule
                    ? $"GGUF 不会被修改。确认后将卸载当前实例、加载 {plan.TemplatePreview.RuleVersion} 并执行四阶段探测；任何失败都会确定性恢复 {plan.OriginalRuntimeTemplate.RuleVersion}，不会错误退回内置模板。"
                    : "GGUF 不会被修改。确认后将卸载当前实例、以完整原配置加载运行时补丁并执行四阶段探测；任何失败都会尝试恢复原始内置模板。"
                : "这是非变更预览：不会卸载/加载模型，也不会修改 Codex 配置。",
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
            .AppendLine(plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule
                ? $"- load、配置一致性或四阶段探测任一步失败：卸载 v3 实例，并从未变化的 GGUF 确定性重建、校验 SHA 后恢复 {plan.OriginalRuntimeTemplate.RuleVersion}。"
                : "- load、配置一致性或四阶段探测任一步失败：卸载补丁实例并按上述配置重新加载原始内置模板。")
            .AppendLine("- 补丁通过后若取消最终 Codex 配置确认，或 Codex 配置提交失败：同样恢复原始实例。")
            .AppendLine("- Codex 配置提交成功：保留补丁实例，直到该实例被卸载。")
            .AppendLine("- selected_variant 只用于量化/文件强校验；/load 的 model 固定使用 REST load key。")
            .AppendLine("- 新 instance ID 以后端响应为准，不预测 :2 后缀。")
            .AppendLine("- 卸载与重新加载可能耗时数分钟；期间请勿启动 Codex 或在其他窗口操作同一模型。");
        return builder.ToString();
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Value<T>(T? value) where T : struct => value?.ToString() ?? "<omitted>";
}
