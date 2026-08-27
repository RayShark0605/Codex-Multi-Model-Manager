namespace CodexModelManager.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        UiExceptionReporter? reporter = null;
        try
        {
            using var composition = new AppComposition();
            reporter = new UiExceptionReporter(composition.Logger, composition.Redactor);
            ThreadExceptionEventHandler handler = CreateThreadExceptionHandler(reporter);
            Application.ThreadException += handler;
            try
            {
                Application.Run(composition.CreateMainForm());
            }
            finally
            {
                Application.ThreadException -= handler;
            }
        }
        catch (Exception exception)
        {
            if (reporter is not null)
            {
                reporter.Report(exception, "程序启动失败");
            }
            else
            {
                try
                {
                    var redactor = new CodexModelManager.Core.Security.SecretRedactor();
                    string message = redactor.Redact($"程序启动失败：{exception.GetType().Name}\n{exception.Message}");
                    MessageBox.Show(message, "Codex Multi-Model Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    MessageBox.Show("程序启动失败。请查看应用日志。", "Codex Multi-Model Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal static ThreadExceptionEventHandler CreateThreadExceptionHandler(UiExceptionReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        return (_, args) => reporter.Report(args.Exception, "未处理的界面错误");
    }
}
