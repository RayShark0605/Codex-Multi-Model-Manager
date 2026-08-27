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
        ProviderCombo.Items.AddRange([ProviderKind.OpenAI, ProviderKind.DeepSeek, ProviderKind.LmStudio]);
        ModelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 720, DisplayMember = nameof(ModelProfile.SelectionLabel) };
        ReasoningCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
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
    public ComboBox SecondaryPolicyCombo { get; }
    public CheckedListBox SecondaryOverridesList { get; }
    public Label OverrideWarningValue { get; }
    public Button RefreshButton { get; }
    public Button PreviewButton { get; }
    public Button SwitchButton { get; }
}
