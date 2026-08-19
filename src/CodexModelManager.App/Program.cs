namespace CodexModelManager.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        try
        {
            using var composition = new AppComposition();
            Application.Run(composition.CreateMainForm());
        }
        catch (Exception exception)
        {
            MessageBox.Show($"程序启动失败：{exception.GetType().Name}\n{exception.Message}", "Codex Multi-Model Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
