namespace CodexModelManager.App.UI;

/// <summary>
/// 主窗体：五个功能页（切换、LM Studio、兼容性、备份、设置日志）；
/// 关闭流程先经控制器做异步收尾（拒绝直接关闭），收尾通过后再真正关窗。
/// </summary>
public sealed class MainForm : Form
{
    private MainController? controller;
    private bool controlledCloseApproved;
    private bool controlledCloseInProgress;

    /// <summary>构造窗体并布置全部页签。</summary>
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

    /// <summary>当前状态与切换页。</summary>
    public CurrentSwitchControl Current { get; }

    /// <summary>LM Studio 页。</summary>
    public LmStudioControl LmStudio { get; }

    /// <summary>兼容性测试页。</summary>
    public CompatibilityControl Compatibility { get; }

    /// <summary>备份历史页。</summary>
    public BackupHistoryControl Backups { get; }

    /// <summary>设置与日志页。</summary>
    public SettingsLogControl SettingsLog { get; }

    /// <summary>装配主控制器并挂接初始化/关闭事件。</summary>
    internal void AttachController(MainController value)
    {
        controller = value;
        Shown += async (_, _) => await controller.InitializeAsync();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => controller.Dispose();
    }

    /// <summary>受控关闭：先取消本次关闭并异步收尾，收尾完成后重新触发真正的关闭。</summary>
    private async void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (controlledCloseApproved || controller is null)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (controlledCloseInProgress)
        {
            return;
        }

        controlledCloseInProgress = true;
        await controller.PrepareForCloseAsync();
        controlledCloseApproved = true;
        if (!IsDisposed && !Disposing)
        {
            Close();
        }
    }

    /// <summary>创建承载指定内容控件的页签。</summary>
    private static TabPage CreateTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }
}
