using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

public sealed class CurrentSwitchControl : UserControl
{
    public CurrentSwitchControl()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        var table = UiFactory.FormTable();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        CodexVersionValue = UiFactory.Label("检测中…");
        CodexStatusValue = UiFactory.Label("检测中…", true);
        CodexHomeValue = UiFactory.Label(string.Empty);
        CurrentProviderValue = UiFactory.Label(string.Empty);
        CurrentModelValue = UiFactory.Label(string.Empty);
        ProviderCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        ProviderCombo.Items.AddRange([ProviderKind.OpenAI, ProviderKind.DeepSeek, ProviderKind.LmStudio, ProviderKind.GLM]);
        ModelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 720, DisplayMember = nameof(ModelProfile.SelectionLabel) };
        ReasoningCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        GlmPlatformLabel = UiFactory.Label("GLM 平台");
        GlmPlatformCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        GlmPlatformCombo.Items.AddRange([new GlmPlatformOption(GlmPlatform.BigModel), new GlmPlatformOption(GlmPlatform.Zai)]);
        GlmPlatformCombo.SelectedIndex = 0;
        SecondaryPolicyCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        SecondaryPolicyCombo.Items.AddRange([SecondaryOverridePolicy.Preserve, SecondaryOverridePolicy.FollowMain, SecondaryOverridePolicy.RestoreOriginal]);
        SecondaryPolicyCombo.SelectedItem = SecondaryOverridePolicy.Preserve;
        SecondaryOverridesList = new CheckedListBox { Width = 720, Height = 150, CheckOnClick = true, HorizontalScrollbar = true };
        OverrideWarningValue = UiFactory.Label("尚未扫描");
        OverrideWarningValue.MaximumSize = new Size(720, 0);

        UiFactory.AddRow(table, "Codex 版本", CodexVersionValue);
        UiFactory.AddRow(table, "Codex 状态", CodexStatusValue);
        UiFactory.AddRow(table, "CODEX_HOME", CodexHomeValue);
        UiFactory.AddRow(table, "当前 Provider", CurrentProviderValue);
        UiFactory.AddRow(table, "当前 Model", CurrentModelValue);
        UiFactory.AddRow(table, "目标 Provider", ProviderCombo);
        UiFactory.AddRow(table, "目标 Model", ModelCombo);
        UiFactory.AddRow(table, "Reasoning", ReasoningCombo);
        // Manual row so the label+combo pair can be hidden as a whole; an AutoSize
        // TableLayoutPanel row with only invisible controls collapses to zero height.
        int glmRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(GlmPlatformLabel, 0, glmRow);
        GlmPlatformCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        GlmPlatformCombo.Margin = new Padding(6);
        table.Controls.Add(GlmPlatformCombo, 1, glmRow);
        SetGlmPlatformRowVisible(false);
        UiFactory.AddRow(table, "Secondary Overrides", SecondaryPolicyCombo);
        UiFactory.AddRow(table, "逐项选择", SecondaryOverridesList);
        UiFactory.AddRow(table, "云调用提示", OverrideWarningValue);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        RefreshButton = UiFactory.Button("重新检测");
        PreviewButton = UiFactory.Button("Preview Changes", 145);
        SwitchButton = UiFactory.Button("Switch Model", 145);
        buttons.Controls.AddRange([RefreshButton, PreviewButton, SwitchButton]);
        Controls.Add(table);
        Controls.Add(buttons);
    }

    public Label CodexVersionValue { get; }
    public Label CodexStatusValue { get; }
    public Label CodexHomeValue { get; }
    public Label CurrentProviderValue { get; }
    public Label CurrentModelValue { get; }
    public ComboBox ProviderCombo { get; }
    public ComboBox ModelCombo { get; }
    public ComboBox ReasoningCombo { get; }
    public Label GlmPlatformLabel { get; }
    public ComboBox GlmPlatformCombo { get; }

    // Control.Visible reports the composite state with the (possibly unshown) parent
    // chain, so the requested state is tracked separately for callers and tests.
    public bool GlmPlatformRowVisible { get; private set; }

    public void SetGlmPlatformRowVisible(bool visible)
    {
        GlmPlatformRowVisible = visible;
        GlmPlatformLabel.Visible = visible;
        GlmPlatformCombo.Visible = visible;
    }
    public ComboBox SecondaryPolicyCombo { get; }
    public CheckedListBox SecondaryOverridesList { get; }
    public Label OverrideWarningValue { get; }
    public Button RefreshButton { get; }
    public Button PreviewButton { get; }
    public Button SwitchButton { get; }

    internal sealed record GlmPlatformOption(GlmPlatform Platform)
    {
        public override string ToString() => Platform switch
        {
            GlmPlatform.BigModel => "智谱国内 (bigmodel.cn)",
            GlmPlatform.Zai => "国际 Z.ai",
            _ => Platform.ToString(),
        };
    }
}
