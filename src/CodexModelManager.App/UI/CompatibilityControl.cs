namespace CodexModelManager.App.UI;

public sealed class CompatibilityControl : UserControl
{
    public CompatibilityControl()
    {
        Dock = DockStyle.Fill;
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        ValidateButton = UiFactory.Button("Validate (L1/L2)", 150);
        SmokeButton = UiFactory.Button("Full Smoke Test (L3)", 175);
        header.Controls.AddRange([ValidateButton, SmokeButton, UiFactory.Label("DeepSeek 测试可能产生少量 API 费用；L3 仅使用独立临时目录。")]);
        Results = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
        };
        Results.Columns.Add("capability", "Capability");
        Results.Columns.Add("status", "Status");
        Results.Columns.Add("failureCode", "Failure Code");
        Results.Columns.Add("detail", "Detail");
        Controls.Add(Results);
        Controls.Add(header);
    }

    public Button ValidateButton { get; }
    public Button SmokeButton { get; }
    public DataGridView Results { get; }
}
