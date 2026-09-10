using System.Globalization;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 应用日志器：写入按日期命名的本地日志文件，并在写入前对敏感信息做脱敏处理。
/// 线程安全（内部以 gate 锁保护文件写入），同时通过事件把日志行广播给订阅者（如 UI）。
/// </summary>
public sealed class AppLogger : IAppLogger, IDisposable
{
    private readonly object gate = new();
    private readonly SecretRedactor redactor;
    private readonly StreamWriter? writer;
    private bool disposed;

    /// <summary>
    /// 构造日志器。<paramref name="writeToDisk"/> 为 false 时仅做脱敏与事件广播，不落盘（用于测试）。
    /// </summary>
    public AppLogger(AppPaths paths, SecretRedactor redactor, bool writeToDisk = true)
    {
        this.redactor = redactor;
        if (!writeToDisk)
        {
            return;
        }

        paths.EnsureDirectories();
        var file = Path.Combine(paths.LogsDirectory, $"cmm-{DateTime.Now:yyyyMMdd}.log");
        writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    /// <summary>每写入一条日志后触发，参数为完整的日志行（已脱敏、已去换行）。</summary>
    public event EventHandler<string>? MessageLogged;

    /// <summary>记录 INFO 级别日志。</summary>
    public void Info(string message) => Write("INFO", message);

    /// <summary>记录 WARN 级别日志。</summary>
    public void Warning(string message) => Write("WARN", message);

    /// <summary>记录 ERROR 级别日志；带异常时附带异常类型与消息。</summary>
    public void LogError(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : string.Create(CultureInfo.InvariantCulture, $"{message} [{exception.GetType().Name}] {exception.Message}");
        Write("ERROR", detail);
    }

    /// <summary>释放底层日志文件流（重复调用安全）。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writer?.Dispose();
    }

    /// <summary>统一写入口：脱敏、压平换行、加时间戳与级别前缀后落盘并广播。</summary>
    private void Write(string level, string message)
    {
        var safe = redactor.Redact(message).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var line = $"{DateTimeOffset.Now:O} [{level}] {safe}";
        lock (gate)
        {
            writer?.WriteLine(line);
        }

        MessageLogged?.Invoke(this, line);
    }
}
