namespace CodexModelManager.App.UI;

/// <summary>兼容性测试页控件：L1/L2 校验与 L3 冒烟测试按钮 + 结果表格。</summary>
public sealed class CompatibilityControl : UserControl
{
    /// <summary>构造控件并布置按钮与结果表格。</summary>
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

    /// <summary>L1/L2 校验按钮。</summary>
    public Button ValidateButton { get; }

    /// <summary>L3 冒烟测试按钮。</summary>
    public Button SmokeButton { get; }

    /// <summary>结果表格。</summary>
    public DataGridView Results { get; }
}
