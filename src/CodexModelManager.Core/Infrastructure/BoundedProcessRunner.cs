using System.Diagnostics;
using System.Text;

namespace CodexModelManager.Core.Infrastructure;

internal sealed record BoundedProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class ProcessOutputLimitException : IOException
{
    public ProcessOutputLimitException(int maximumBytes)
        : base($"CLI 输出超过 {maximumBytes:N0} 字节上限；未使用截断输出。")
    {
    }
}

internal static class BoundedProcessRunner
{
    internal const int StatusOutputLimit = 256 * 1024;
    internal const int CatalogOutputLimit = 16 * 1024 * 1024;
    internal const int SmokeErrorOutputLimit = 1024 * 1024;

    public static Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo start,
        TimeSpan timeout,
        int maximumOutputBytes,
        int maximumErrorBytes,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        bool combineOutputBudget = false) => ExecuteAsync(
            start, timeout, maximumOutputBytes, maximumErrorBytes, combineOutputBudget,
            async (process, output, error, token) =>
            {
                process.StandardInput.Close();
                Task<string> outputTask = output.ReadToEndAsync(token, onOutputLine);
                await Task.WhenAll(process.WaitForExitAsync(token), outputTask, error).ConfigureAwait(false);
                return new BoundedProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await error.ConfigureAwait(false));
            }, cancellationToken);

    public static Task<T> RunProtocolAsync<T>(
        ProcessStartInfo start,
        TimeSpan timeout,
        int maximumCombinedBytes,
        Func<BoundedProcessConnection, CancellationToken, Task<T>> protocol,
        CancellationToken cancellationToken) => ExecuteAsync(
            start, timeout, maximumCombinedBytes, maximumCombinedBytes, true,
            (process, output, _, token) => protocol(new BoundedProcessConnection(process.StandardInput, output), token),
            cancellationToken);

    private static async Task<T> ExecuteAsync<T>(
        ProcessStartInfo start,
        TimeSpan timeout,
        int maximumOutputBytes,
        int maximumErrorBytes,
        bool combineOutputBudget,
        Func<Process, BoundedUtf8LineReader, Task<string>, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var outputBudget = new ProcessOutputBudget(maximumOutputBytes);
        var errorBudget = combineOutputBudget ? outputBudget : new ProcessOutputBudget(maximumErrorBytes);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = Encoding.UTF8;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 CLI 子进程。");
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ReportFailure(Exception exception)
        {
            if (exception is not OperationCanceledException && !deadline.IsCancellationRequested) failure.TrySetResult(exception);
        }
        var output = new BoundedUtf8LineReader(process.StandardOutput.BaseStream, outputBudget, ReportFailure);
        var error = new BoundedUtf8LineReader(process.StandardError.BaseStream, errorBudget, ReportFailure);
        Task<string> errorTask = error.ReadToEndAsync(deadline.Token);
        Task<T>? operationTask = null;
        try
        {
            operationTask = operation(process, output, errorTask, deadline.Token);
            await Task.WhenAny(operationTask, failure.Task).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (failure.Task.IsCompletedSuccessfully)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(await failure.Task.ConfigureAwait(false)).Throw();
            }
            return await operationTask.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            await BoundedProcessCleanup.TerminateAndDrainAsync(
                process, operationTask is null ? [errorTask] : [errorTask, operationTask]).ConfigureAwait(false);
        }
    }
}

internal sealed class BoundedProcessConnection(StreamWriter input, BoundedUtf8LineReader output)
{
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => output.ReadLineAsync(cancellationToken);

    public async Task WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        await input.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ProcessOutputBudget
{
    private readonly int maximumBytes;
    private long consumedBytes;

    public ProcessOutputBudget(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.maximumBytes = maximumBytes;
    }

    public void Consume(int count)
    {
        if (Interlocked.Add(ref consumedBytes, count) > maximumBytes) throw new ProcessOutputLimitException(maximumBytes);
    }
}

internal sealed class BoundedUtf8LineReader(Stream stream, ProcessOutputBudget budget, Action<Exception>? onFailure = null)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly byte[] buffer = new byte[4096];
    private int position;
    private int length;
    private bool skipLineFeed;
    private bool firstLine = true;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var line = new MemoryStream();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (position == length)
                {
                    length = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    position = 0;
                    if (length == 0) return line.Length == 0 ? null : Decode(line);
                    budget.Consume(length);
                }

                byte value = buffer[position++];
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (value == '\n') continue;
                }
                if (value is (byte)'\r' or (byte)'\n')
                {
                    skipLineFeed = value == '\r';
                    return Decode(line);
                }
                line.WriteByte(value);
            }
        }
        catch (Exception exception)
        {
            onFailure?.Invoke(exception);
            throw;
        }
    }

    public async Task<string> ReadToEndAsync(CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        try
        {
            var text = onLine is null ? new StringBuilder() : null;
            while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                if (onLine is null) text!.AppendLine(line);
                else onLine(line);
            }
            return text?.ToString() ?? string.Empty;
        }
        catch (Exception exception)
        {
            onFailure?.Invoke(exception);
            throw;
        }
    }

    private string Decode(MemoryStream line)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new IOException("CLI 输出包含无效 UTF-8。", exception);
        }
        if (firstLine)
        {
            firstLine = false;
            return text.TrimStart('\uFEFF');
        }
        return text;
    }
}
