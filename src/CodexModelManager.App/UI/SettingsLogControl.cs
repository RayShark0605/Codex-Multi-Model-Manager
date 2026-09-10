namespace CodexModelManager.App.UI;

/// <summary>设置/日志页控件：三个 Provider 的 Token 录入区与只读日志视图。</summary>
public sealed class SettingsLogControl : UserControl
{
    /// <summary>构造控件并布置凭据表与日志框。</summary>
    public SettingsLogControl()
    {
        Dock = DockStyle.Fill;
        var credentials = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12) };
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        DeepSeekToken = new TextBox { UseSystemPasswordChar = true, Width = 420, PlaceholderText = "仅写入 Windows Credential Manager" };
        LmStudioToken = new TextBox { UseSystemPasswordChar = true, Width = 420, PlaceholderText = "仅在 LM Studio 开启认证时需要" };
        GlmToken = new TextBox { UseSystemPasswordChar = true, Width = 420, PlaceholderText = "GLM Coding Plan API Key（国内/国际通用）" };
        SaveDeepSeekButton = UiFactory.Button("保存 DeepSeek", 130);
        SaveLmStudioButton = UiFactory.Button("保存 LM Token", 130);
        SaveGlmButton = UiFactory.Button("保存 GLM", 130);
        credentials.Controls.Add(UiFactory.Label("DeepSeek API Token"), 0, 0);
        credentials.Controls.Add(DeepSeekToken, 1, 0);
        credentials.Controls.Add(SaveDeepSeekButton, 2, 0);
        credentials.Controls.Add(UiFactory.Label("LM Studio API Token"), 0, 1);
        credentials.Controls.Add(LmStudioToken, 1, 1);
        credentials.Controls.Add(SaveLmStudioButton, 2, 1);
        credentials.Controls.Add(UiFactory.Label("GLM API Token"), 0, 2);
        credentials.Controls.Add(GlmToken, 1, 2);
        credentials.Controls.Add(SaveGlmButton, 2, 2);
        CredentialStatus = UiFactory.Label("凭据状态：检测中…");
        credentials.Controls.Add(CredentialStatus, 0, 3);
        credentials.SetColumnSpan(CredentialStatus, 3);
        Log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            MaxLength = 1_000_000,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9),
        };
        Controls.Add(Log);
        Controls.Add(credentials);
    }

    /// <summary>DeepSeek Token 输入框。</summary>
    public TextBox DeepSeekToken { get; }

    /// <summary>LM Studio Token 输入框。</summary>
    public TextBox LmStudioToken { get; }

    /// <summary>GLM Token 输入框。</summary>
    public TextBox GlmToken { get; }

    /// <summary>保存 DeepSeek Token 按钮。</summary>
    public Button SaveDeepSeekButton { get; }

    /// <summary>保存 LM Studio Token 按钮。</summary>
    public Button SaveLmStudioButton { get; }

    /// <summary>保存 GLM Token 按钮。</summary>
    public Button SaveGlmButton { get; }

    /// <summary>凭据状态标签。</summary>
    public Label CredentialStatus { get; }

    /// <summary>只读日志文本框。</summary>
    public TextBox Log { get; }
}
