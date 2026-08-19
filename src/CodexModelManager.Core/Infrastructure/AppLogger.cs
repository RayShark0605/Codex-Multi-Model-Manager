using System.Globalization;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Core.Infrastructure;

public sealed class AppLogger : IAppLogger, IDisposable
{
    private readonly object gate = new();
    private readonly SecretRedactor redactor;
    private readonly StreamWriter? writer;
    private bool disposed;

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

    public event EventHandler<string>? MessageLogged;

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void LogError(string message, Exception? exception = null)
    {
        var detail = exception is null
            ? message
            : string.Create(CultureInfo.InvariantCulture, $"{message} [{exception.GetType().Name}] {exception.Message}");
        Write("ERROR", detail);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writer?.Dispose();
    }

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
