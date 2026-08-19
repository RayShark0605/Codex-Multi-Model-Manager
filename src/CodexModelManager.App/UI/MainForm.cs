namespace CodexModelManager.App.UI;

public sealed class MainForm : Form
{
    private MainController? controller;

    public MainForm()
    {
        Text = "Codex Multi-Model Manager";
        MinimumSize = new Size(980, 700);
        Size = new Size(1180, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Current = new CurrentSwitchControl();
        LmStudio = new LmStudioControl();
        Compatibility = new CompatibilityControl();
        Backups = new BackupHistoryControl();
        SettingsLog = new SettingsLogControl();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateTab("当前状态与切换", Current));
        tabs.TabPages.Add(CreateTab("LM Studio", LmStudio));
        tabs.TabPages.Add(CreateTab("兼容性测试", Compatibility));
        tabs.TabPages.Add(CreateTab("备份历史", Backups));
        tabs.TabPages.Add(CreateTab("设置与日志", SettingsLog));
        Controls.Add(tabs);
    }

    public CurrentSwitchControl Current { get; }
    public LmStudioControl LmStudio { get; }
    public CompatibilityControl Compatibility { get; }
    public BackupHistoryControl Backups { get; }
    public SettingsLogControl SettingsLog { get; }

    internal void AttachController(MainController value)
    {
        controller = value;
        Shown += async (_, _) => await controller.InitializeAsync();
        FormClosed += (_, _) => controller.Dispose();
    }

    private static TabPage CreateTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }
}
