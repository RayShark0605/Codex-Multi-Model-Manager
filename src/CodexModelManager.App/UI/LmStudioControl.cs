using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

/// <summary>LM Studio 页控件：端点/模型信息、上下文配置、指令层级检测与模板修复操作。</summary>
public sealed class LmStudioControl : UserControl
{
    /// <summary>构造控件并布置全部信息行、模板操作与页级按钮。</summary>
    public LmStudioControl()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        var table = UiFactory.FormTable();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        EndpointText = new TextBox { Text = "http://127.0.0.1:1234", Width = 420 };
        ServerStatusValue = UiFactory.Label("尚未检测", true);
        VersionValue = UiFactory.Label("未知");
        ModelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 760, DisplayMember = nameof(ModelProfile.SelectionLabel) };
        LoadedValue = UiFactory.Label("未知");
        QuantValue = UiFactory.Label("未知");
        ToolUseValue = UiFactory.Label("未知");
        ReasoningValue = UiFactory.Label("未知");
        MaxContextValue = UiFactory.Label("未知");
        LoadedContextValue = UiFactory.Label("未知");
        EffectiveContextValue = UiFactory.Label("未知");
        CodexContextInput = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Width = 180, ThousandsSeparator = true };
        AutoCompactInput = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Width = 180, ThousandsSeparator = true, Enabled = false };
        AutoCompactAutomaticCheckBox = new CheckBox { Text = "自动建议", AutoSize = true, Checked = true, Margin = new Padding(10, 8, 6, 6) };
        ResetAutoCompactButton = UiFactory.Button("恢复建议值", 110);
        var autoCompactPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        autoCompactPanel.Controls.AddRange([AutoCompactInput, AutoCompactAutomaticCheckBox, ResetAutoCompactButton]);
        ToolOutputLimitValue = UiFactory.Label("未知（自动）");
        ToolOutputLimitValue.MaximumSize = new Size(760, 0);
        ContextWarningValue = UiFactory.Label("请选择 loaded model。");
        ContextWarningValue.MaximumSize = new Size(760, 0);
        ContextWarningValue.ForeColor = Color.DarkOrange;
        DiscoverySourceValue = UiFactory.Label("未知");
        HierarchyStatusValue = UiFactory.Label("Untested", true);
        HierarchyStatusValue.ForeColor = Color.DarkOrange;
        BasicControlValue = UiFactory.Label("Not run");
        LeadingDeveloperValue = UiFactory.Label("Not run");
        ConversationControlValue = UiFactory.Label("Not run");
        ContinuationDeveloperValue = UiFactory.Label("Not run");
        HierarchyDetailValue = UiFactory.Label("尚未对当前 loaded instance 执行四阶段差分检测。");
        GgufPathText = new TextBox { Width = 650 };
        BrowseGgufButton = UiFactory.Button("选择 GGUF", 110);
        var ggufPathPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        ggufPathPanel.Controls.AddRange([GgufPathText, BrowseGgufButton]);
        TemplateStatusValue = UiFactory.Label("尚未分析");
        PersistenceStatusValue = UiFactory.Label("Persistence State Ambiguous — 尚未刷新精确模型状态", true);
        PersistenceStatusValue.MaximumSize = new Size(760, 0);
        PersistenceStatusValue.ForeColor = Color.DarkOrange;
        RuntimeRepairStatusValue = UiFactory.Label("Idle");
        RuntimeRepairStatusValue.MaximumSize = new Size(760, 0);
        AnalyzeTemplateButton = UiFactory.Button("分析 Prompt Template", 165);
        ExportTemplateButton = UiFactory.Button("导出兼容模板", 145);
        CopyTemplateButton = UiFactory.Button("复制兼容模板", 145);
        RecheckHierarchyButton = UiFactory.Button("重新检测 Codex 指令层级", 205);
        AnalyzeTemplateButton.Enabled = false;
        ExportTemplateButton.Enabled = false;
        CopyTemplateButton.Enabled = false;
        var templateButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        templateButtons.Controls.AddRange([AnalyzeTemplateButton, ExportTemplateButton, CopyTemplateButton, RecheckHierarchyButton]);

        UiFactory.AddRow(table, "Endpoint", EndpointText);
        UiFactory.AddRow(table, "Server", ServerStatusValue);
        UiFactory.AddRow(table, "LM Studio 版本", VersionValue);
        UiFactory.AddRow(table, "模型 / loaded instance", ModelCombo);
        UiFactory.AddRow(table, "Loaded", LoadedValue);
        UiFactory.AddRow(table, "Type / Quant / Params", QuantValue);
        UiFactory.AddRow(table, "Tool Use", ToolUseValue);
        UiFactory.AddRow(table, "Reasoning", ReasoningValue);
        UiFactory.AddRow(table, "Model Max Context", MaxContextValue);
        UiFactory.AddRow(table, "Loaded Context", LoadedContextValue);
        UiFactory.AddRow(table, "Codex Effective Context（95%）", EffectiveContextValue);
        UiFactory.AddRow(table, "Codex Configured Context", CodexContextInput);
        UiFactory.AddRow(table, "Auto Compact", autoCompactPanel);
        UiFactory.AddRow(table, "Tool Output Limit（自动）", ToolOutputLimitValue);
        UiFactory.AddRow(table, "Context 检查", ContextWarningValue);
        UiFactory.AddRow(table, "发现来源", DiscoverySourceValue);
        UiFactory.AddRow(table, "Codex Instruction Hierarchy", HierarchyStatusValue);
        UiFactory.AddRow(table, "Basic Control", BasicControlValue);
        UiFactory.AddRow(table, "Leading Developer", LeadingDeveloperValue);
        UiFactory.AddRow(table, "Conversation Control", ConversationControlValue);
        UiFactory.AddRow(table, "Continuation Developer", ContinuationDeveloperValue);
        UiFactory.AddRow(table, "层级检测详情", HierarchyDetailValue);
        UiFactory.AddRow(table, "对应 GGUF（只读）", ggufPathPanel);
        UiFactory.AddRow(table, "Prompt Template", TemplateStatusValue);
        UiFactory.AddRow(table, "持久 Prompt Template", PersistenceStatusValue);
        UiFactory.AddRow(table, "运行时修复事务", RuntimeRepairStatusValue);
        UiFactory.AddRow(table, "模板修复操作", templateButtons);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        DetectButton = UiFactory.Button("检测 Server");
        RefreshModelsButton = UiFactory.Button("刷新模型");
        RecoverTransactionButton = UiFactory.Button("检查/恢复未完成事务", 190);
        RecoverTransactionButton.Enabled = false;
        buttons.Controls.AddRange([DetectButton, RefreshModelsButton, RecoverTransactionButton]);
        Controls.Add(table);
        Controls.Add(buttons);
    }

    /// <summary>端点输入框。</summary>
    public TextBox EndpointText { get; }

    /// <summary>Server 状态标签。</summary>
    public Label ServerStatusValue { get; }

    /// <summary>LM Studio 版本标签。</summary>
    public Label VersionValue { get; }

    /// <summary>模型下拉框。</summary>
    public ComboBox ModelCombo { get; }

    /// <summary>加载状态标签。</summary>
    public Label LoadedValue { get; }

    /// <summary>类型/量化/参数标签。</summary>
    public Label QuantValue { get; }

    /// <summary>Tool Use 能力标签。</summary>
    public Label ToolUseValue { get; }

    /// <summary>Reasoning 能力标签。</summary>
    public Label ReasoningValue { get; }

    /// <summary>模型最大上下文标签。</summary>
    public Label MaxContextValue { get; }

    /// <summary>已加载上下文标签。</summary>
    public Label LoadedContextValue { get; }

    /// <summary>Codex 有效上下文标签。</summary>
    public Label EffectiveContextValue { get; }

    /// <summary>Codex 配置上下文输入框。</summary>
    public NumericUpDown CodexContextInput { get; }

    /// <summary>自动压缩阈值输入框。</summary>
    public NumericUpDown AutoCompactInput { get; }

    /// <summary>自动压缩“自动建议”复选框。</summary>
    public CheckBox AutoCompactAutomaticCheckBox { get; }

    /// <summary>恢复自动压缩建议值按钮。</summary>
    public Button ResetAutoCompactButton { get; }

    /// <summary>工具输出上限标签。</summary>
    public Label ToolOutputLimitValue { get; }

    /// <summary>上下文检查警告标签。</summary>
    public Label ContextWarningValue { get; }

    /// <summary>模型发现来源标签。</summary>
    public Label DiscoverySourceValue { get; }

    /// <summary>指令层级总状态标签。</summary>
    public Label HierarchyStatusValue { get; }

    /// <summary>Basic Control 步骤标签。</summary>
    public Label BasicControlValue { get; }

    /// <summary>Leading Developer 步骤标签。</summary>
    public Label LeadingDeveloperValue { get; }

    /// <summary>Conversation Control 步骤标签。</summary>
    public Label ConversationControlValue { get; }

    /// <summary>Continuation Developer 步骤标签。</summary>
    public Label ContinuationDeveloperValue { get; }

    /// <summary>层级检测详情标签。</summary>
    public Label HierarchyDetailValue { get; }

    /// <summary>GGUF 路径输入框（只读分析用）。</summary>
    public TextBox GgufPathText { get; }

    /// <summary>Prompt Template 状态标签。</summary>
    public Label TemplateStatusValue { get; }

    /// <summary>持久化模板状态标签。</summary>
    public Label PersistenceStatusValue { get; }

    /// <summary>运行时修复事务状态标签。</summary>
    public Label RuntimeRepairStatusValue { get; }

    /// <summary>检测 Server 按钮。</summary>
    public Button DetectButton { get; }

    /// <summary>刷新模型按钮。</summary>
    public Button RefreshModelsButton { get; }

    /// <summary>检查/恢复未完成事务按钮。</summary>
    public Button RecoverTransactionButton { get; }

    /// <summary>选择 GGUF 按钮。</summary>
    public Button BrowseGgufButton { get; }

    /// <summary>分析 Prompt Template 按钮。</summary>
    public Button AnalyzeTemplateButton { get; }

    /// <summary>导出兼容模板按钮。</summary>
    public Button ExportTemplateButton { get; }

    /// <summary>复制兼容模板按钮。</summary>
    public Button CopyTemplateButton { get; }

    /// <summary>重新检测 Codex 指令层级按钮。</summary>
    public Button RecheckHierarchyButton { get; }
}
