using CodexModelManager.Core.Models;

namespace CodexModelManager.App.UI;

/// <summary>主切换页控件：环境信息、目标 Provider/模型选择、Secondary Override 管理与操作按钮。</summary>
public sealed class CurrentSwitchControl : UserControl
{
    /// <summary>构造控件并布置全部行与按钮。</summary>
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
        // GLM 平台行手工布局，便于把“标签+下拉框”整行隐藏；
        // AutoSize 表格行内只剩不可见控件时会塌缩成零高度。
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

    /// <summary>Codex 版本标签。</summary>
    public Label CodexVersionValue { get; }

    /// <summary>Codex 状态标签（加粗）。</summary>
    public Label CodexStatusValue { get; }

    /// <summary>CODEX_HOME 标签。</summary>
    public Label CodexHomeValue { get; }

    /// <summary>当前 Provider 标签。</summary>
    public Label CurrentProviderValue { get; }

    /// <summary>当前 Model 标签。</summary>
    public Label CurrentModelValue { get; }

    /// <summary>目标 Provider 下拉框。</summary>
    public ComboBox ProviderCombo { get; }

    /// <summary>目标模型下拉框。</summary>
    public ComboBox ModelCombo { get; }

    /// <summary>Reasoning 下拉框。</summary>
    public ComboBox ReasoningCombo { get; }

    /// <summary>GLM 平台行标签。</summary>
    public Label GlmPlatformLabel { get; }

    /// <summary>GLM 平台下拉框。</summary>
    public ComboBox GlmPlatformCombo { get; }

    /// <summary>
    /// GLM 平台行的请求可见状态。Control.Visible 反映的是与（可能尚未显示的）
    /// 父级链的复合状态，因此这里单独记录，供调用方与测试判断。
    /// </summary>
    public bool GlmPlatformRowVisible { get; private set; }

    /// <summary>设置 GLM 平台行（标签+下拉框）整体的可见性。</summary>
    public void SetGlmPlatformRowVisible(bool visible)
    {
        GlmPlatformRowVisible = visible;
        GlmPlatformLabel.Visible = visible;
        GlmPlatformCombo.Visible = visible;
    }

    /// <summary>Secondary Override 策略下拉框。</summary>
    public ComboBox SecondaryPolicyCombo { get; }

    /// <summary>Secondary Override 逐项选择列表。</summary>
    public CheckedListBox SecondaryOverridesList { get; }

    /// <summary>云调用提示标签。</summary>
    public Label OverrideWarningValue { get; }

    /// <summary>重新检测按钮。</summary>
    public Button RefreshButton { get; }

    /// <summary>预览变更按钮。</summary>
    public Button PreviewButton { get; }

    /// <summary>切换模型按钮。</summary>
    public Button SwitchButton { get; }

    /// <summary>GLM 平台选项（携带平台枚举的展示项）。</summary>
    internal sealed record GlmPlatformOption(GlmPlatform Platform)
    {
        /// <summary>下拉框展示文本。</summary>
        public override string ToString() => Platform switch
        {
            GlmPlatform.BigModel => "智谱国内 (bigmodel.cn)",
            GlmPlatform.Zai => "国际 Z.ai",
            _ => Platform.ToString(),
        };
    }
}
