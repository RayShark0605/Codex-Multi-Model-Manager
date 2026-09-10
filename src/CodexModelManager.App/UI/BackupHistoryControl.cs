namespace CodexModelManager.App.UI;

/// <summary>备份历史页控件：刷新/恢复按钮组与历史快照列表。</summary>
public sealed class BackupHistoryControl : UserControl
{
    /// <summary>构造控件并布置按钮与历史列表。</summary>
    public BackupHistoryControl()
    {
        Dock = DockStyle.Fill;
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        RefreshButton = UiFactory.Button("刷新历史");
        RestorePreviousButton = UiFactory.Button("恢复上一次", 130);
        RestoreSelectedButton = UiFactory.Button("恢复所选", 130);
        RestoreInitialButton = UiFactory.Button("恢复 Initial Snapshot", 175);
        InspectDeepSeekButton = UiFactory.Button("查看 backup-deepseek", 180);
        header.Controls.AddRange([RefreshButton, RestorePreviousButton, RestoreSelectedButton, RestoreInitialButton, InspectDeepSeekButton]);
        History = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        History.Columns.Add("时间", 190);
        History.Columns.Add("操作", 130);
        History.Columns.Add("来源", 170);
        History.Columns.Add("目标", 170);
        History.Columns.Add("SHA", 90);
        Controls.Add(History);
        Controls.Add(header);
    }

    /// <summary>刷新历史按钮。</summary>
    public Button RefreshButton { get; }

    /// <summary>恢复上一次快照按钮。</summary>
    public Button RestorePreviousButton { get; }

    /// <summary>恢复所选快照按钮。</summary>
    public Button RestoreSelectedButton { get; }

    /// <summary>恢复初始快照按钮。</summary>
    public Button RestoreInitialButton { get; }

    /// <summary>查看 backup-deepseek 目录按钮。</summary>
    public Button InspectDeepSeekButton { get; }

    /// <summary>历史快照列表。</summary>
    public ListView History { get; }
}
