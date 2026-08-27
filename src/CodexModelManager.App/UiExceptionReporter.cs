using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Security;

namespace CodexModelManager.App;

internal sealed class UiExceptionReporter
{
    private const string FallbackMessage = "程序遇到未处理的界面错误。错误详情无法安全显示，请查看应用日志。";
    private readonly IAppLogger logger;
    private readonly SecretRedactor redactor;
    private readonly Action<string, string, MessageBoxIcon> showMessage;

    public UiExceptionReporter(
        IAppLogger logger,
        SecretRedactor redactor,
        Action<string, string, MessageBoxIcon>? showMessage = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        this.showMessage = showMessage ?? ((message, title, icon) =>
            MessageBox.Show(message, title, MessageBoxButtons.OK, icon));
    }

    public void Report(Exception exception, string title)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            logger.LogError("未处理的 UI 线程异常", exception);
        }
        catch
        {
        }

        string message;
        try
        {
            message = redactor.Redact($"{exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            message = FallbackMessage;
        }

        try
        {
            showMessage(message, title, MessageBoxIcon.Error);
        }
        catch
        {
            try
            {
                showMessage(FallbackMessage, "Codex Multi-Model Manager", MessageBoxIcon.Error);
            }
            catch
            {
            }
        }
    }
}
