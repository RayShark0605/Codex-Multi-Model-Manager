using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

public sealed class LmStudioControl : UserControl
{
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
        CodexContextInput = new NumericUpDown { Minimum = 1, Maximum = 4_000_000, Width = 180, ThousandsSeparator = true };
        AutoCompactInput = new NumericUpDown { Minimum = 1, Maximum = 4_000_000, Width = 180, ThousandsSeparator = true };
        ContextWarningValue = UiFactory.Label("请选择 loaded model。");
        ContextWarningValue.ForeColor = Color.DarkOrange;
        DiscoverySourceValue = UiFactory.Label("未知");
        HierarchyStatusValue = UiFactory.Label("Untested", true);
        HierarchyStatusValue.ForeColor = Color.DarkOrange;
        HierarchyDetailValue = UiFactory.Label("尚未对当前 loaded instance 执行 instructions + developer + user 差分检测。");
        GgufPathText = new TextBox { Width = 650 };
        BrowseGgufButton = UiFactory.Button("选择 GGUF", 110);
        var ggufPathPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        ggufPathPanel.Controls.AddRange([GgufPathText, BrowseGgufButton]);
        TemplateStatusValue = UiFactory.Label("尚未分析");
        AnalyzeTemplateButton = UiFactory.Button("分析 Prompt Template", 165);
        ExportTemplateButton = UiFactory.Button("导出兼容模板", 145);
        CopyTemplateButton = UiFactory.Button("复制兼容模板", 145);
        RecheckHierarchyButton = UiFactory.Button("重新检测 Codex 指令层级", 205);
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
        UiFactory.AddRow(table, "Codex Configured Context", CodexContextInput);
        UiFactory.AddRow(table, "Auto Compact（建议值）", AutoCompactInput);
        UiFactory.AddRow(table, "Context 检查", ContextWarningValue);
        UiFactory.AddRow(table, "发现来源", DiscoverySourceValue);
        UiFactory.AddRow(table, "Codex Instruction Hierarchy", HierarchyStatusValue);
        UiFactory.AddRow(table, "层级检测详情", HierarchyDetailValue);
        UiFactory.AddRow(table, "对应 GGUF（只读）", ggufPathPanel);
        UiFactory.AddRow(table, "Prompt Template", TemplateStatusValue);
        UiFactory.AddRow(table, "模板修复操作", templateButtons);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        DetectButton = UiFactory.Button("检测 Server");
        RefreshModelsButton = UiFactory.Button("刷新模型");
        buttons.Controls.AddRange([DetectButton, RefreshModelsButton]);
        Controls.Add(buttons);
        Controls.Add(table);
    }

    public TextBox EndpointText { get; }
    public Label ServerStatusValue { get; }
    public Label VersionValue { get; }
    public ComboBox ModelCombo { get; }
    public Label LoadedValue { get; }
    public Label QuantValue { get; }
    public Label ToolUseValue { get; }
    public Label ReasoningValue { get; }
    public Label MaxContextValue { get; }
    public Label LoadedContextValue { get; }
    public NumericUpDown CodexContextInput { get; }
    public NumericUpDown AutoCompactInput { get; }
    public Label ContextWarningValue { get; }
    public Label DiscoverySourceValue { get; }
    public Label HierarchyStatusValue { get; }
    public Label HierarchyDetailValue { get; }
    public TextBox GgufPathText { get; }
    public Label TemplateStatusValue { get; }
    public Button DetectButton { get; }
    public Button RefreshModelsButton { get; }
    public Button BrowseGgufButton { get; }
    public Button AnalyzeTemplateButton { get; }
    public Button ExportTemplateButton { get; }
    public Button CopyTemplateButton { get; }
    public Button RecheckHierarchyButton { get; }
}
