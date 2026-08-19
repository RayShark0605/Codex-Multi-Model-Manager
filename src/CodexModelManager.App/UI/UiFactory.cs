namespace CodexModelManager.App.UI;

internal static class UiFactory
{
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

    public static Button Button(string text, int width = 120) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = 32,
        Margin = new Padding(6),
    };

    public static TableLayoutPanel FormTable() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 2,
        Padding = new Padding(12),
    };

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
