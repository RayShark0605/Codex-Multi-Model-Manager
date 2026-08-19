namespace CodexModelManager.App.UI;

public sealed class BackupHistoryControl : UserControl
{
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

    public Button RefreshButton { get; }
    public Button RestorePreviousButton { get; }
    public Button RestoreSelectedButton { get; }
    public Button RestoreInitialButton { get; }
    public Button InspectDeepSeekButton { get; }
    public ListView History { get; }
}
