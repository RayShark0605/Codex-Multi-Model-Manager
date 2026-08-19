namespace CodexModelManager.App.UI;

public sealed class SettingsLogControl : UserControl
{
    public SettingsLogControl()
    {
        Dock = DockStyle.Fill;
        var credentials = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12) };
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        DeepSeekToken = new TextBox { UseSystemPasswordChar = true, Width = 420, PlaceholderText = "仅写入 Windows Credential Manager" };
        LmStudioToken = new TextBox { UseSystemPasswordChar = true, Width = 420, PlaceholderText = "仅在 LM Studio 开启认证时需要" };
        SaveDeepSeekButton = UiFactory.Button("保存 DeepSeek", 130);
        SaveLmStudioButton = UiFactory.Button("保存 LM Token", 130);
        credentials.Controls.Add(UiFactory.Label("DeepSeek API Token"), 0, 0);
        credentials.Controls.Add(DeepSeekToken, 1, 0);
        credentials.Controls.Add(SaveDeepSeekButton, 2, 0);
        credentials.Controls.Add(UiFactory.Label("LM Studio API Token"), 0, 1);
        credentials.Controls.Add(LmStudioToken, 1, 1);
        credentials.Controls.Add(SaveLmStudioButton, 2, 1);
        CredentialStatus = UiFactory.Label("凭据状态：检测中…");
        credentials.Controls.Add(CredentialStatus, 0, 2);
        credentials.SetColumnSpan(CredentialStatus, 3);
        Log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9),
        };
        Controls.Add(Log);
        Controls.Add(credentials);
    }

    public TextBox DeepSeekToken { get; }
    public TextBox LmStudioToken { get; }
    public Button SaveDeepSeekButton { get; }
    public Button SaveLmStudioButton { get; }
    public Label CredentialStatus { get; }
    public TextBox Log { get; }
}
