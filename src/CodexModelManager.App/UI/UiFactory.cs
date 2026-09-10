namespace CodexModelManager.App.UI;

/// <summary>UI 构件工厂：统一标签、按钮与表单表格的样式。</summary>
internal static class UiFactory
{
    /// <summary>创建标签（可选加粗）。</summary>
    public static Label Label(string text, bool bold = false)
    {
        Font baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = bold ? new Font(baseFont, FontStyle.Bold) : baseFont,
            Margin = new Padding(6),
        };
    }

    /// <summary>创建固定尺寸按钮。</summary>
    public static Button Button(string text, int width = 120) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = 32,
        Margin = new Padding(6),
    };

    /// <summary>创建两列表单表格（顶部停靠、自动增高）。</summary>
    public static TableLayoutPanel FormTable() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 2,
        Padding = new Padding(12),
    };

    /// <summary>向表格追加一行“标题 + 控件”。</summary>
    public static void AddRow(TableLayoutPanel table, string title, Control value)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(Label(title), 0, row);
        value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        value.Margin = new Padding(6);
        table.Controls.Add(value, 1, row);
    }
}
