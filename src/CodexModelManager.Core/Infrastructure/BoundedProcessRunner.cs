using System.Diagnostics;
using System.Text;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>一次有界进程执行的返回结果：退出码与标准输出/标准错误的完整文本。</summary>
internal sealed record BoundedProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>CLI 输出超过字节上限时抛出的异常；刻意不做截断，避免拿半份数据继续算。</summary>
internal sealed class ProcessOutputLimitException : IOException
{
    public ProcessOutputLimitException(int maximumBytes)
        : base($"CLI 输出超过 {maximumBytes:N0} 字节上限；未使用截断输出。")
    {
    }
}

/// <summary>
/// 有界进程运行器：以超时、输出字节上限、取消令牌三重约束执行外部 CLI，
/// 统一处理重定向管道的 UTF-8 严格解码、失败上报与进程清理，防止子进程挂死或输出失控。
/// </summary>
internal static class BoundedProcessRunner
{
    /// <summary>常规状态类命令的输出上限（256 KB）。</summary>
    internal const int StatusOutputLimit = 256 * 1024;

    /// <summary>模型目录抓取类命令的输出上限（16 MB）。</summary>
    internal const int CatalogOutputLimit = 16 * 1024 * 1024;

    /// <summary>冒烟测试错误输出的上限（1 MB）。</summary>
    internal const int SmokeErrorOutputLimit = 1024 * 1024;

    /// <summary>
    /// 运行 CLI 并收集全部输出：关闭标准输入，并行等待退出与 stdout/stderr 读完，
    /// 返回退出码与两侧输出文本。
    /// </summary>
    public static Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo start,
        TimeSpan timeout,
        int maximumOutputBytes,
        int maximumErrorBytes,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        bool combineOutputBudget = false) => ExecuteAsync(start, timeout, maximumOutputBytes, maximumErrorBytes, combineOutputBudget, async (process, output, error, token) =>
        {
            process.StandardInput.Close();
            Task<string> outputTask = output.ReadToEndAsync(token, onOutputLine);
            await Task.WhenAll(process.WaitForExitAsync(token), outputTask, error).ConfigureAwait(false);
            return new BoundedProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await error.ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>以“请求-响应”协议方式运行 CLI：调用方通过连接对象按行读写，输出与错误共用同一预算。</summary>
    public static Task<T> RunProtocolAsync<T>(
        ProcessStartInfo start,
        TimeSpan timeout,
        int maximumCombinedBytes,
        Func<BoundedProcessConnection, CancellationToken, Task<T>> protocol,
        CancellationToken cancellationToken) => ExecuteAsync(start, timeout, maximumCombinedBytes, maximumCombinedBytes, true, (process, output, _, token) => protocol(new BoundedProcessConnection(process.StandardInput, output), token), cancellationToken);

    /// <summary>
    /// 执行核心：链接调用方令牌与超时生成截止令牌；启动进程并挂接两个有界读取器；
    /// 竞速等待业务任务与失败通知，任何读取失败都会优先于业务结果抛出；最终保证进程被终止并清空。
    /// </summary>
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
        // 合并预算模式下 stderr 与 stdout 共用同一配额，否则各自独立计量
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
            if (exception is not OperationCanceledException && !deadline.IsCancellationRequested)
            {
                failure.TrySetResult(exception);
            }
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
                // 读取侧已失败：优先抛出读取异常，而不是让业务任务的结果/取消掩盖它
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(await failure.Task.ConfigureAwait(false)).Throw();
            }

            return await operationTask.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            await BoundedProcessCleanup.TerminateAndDrainAsync(process, operationTask is null ? [errorTask] : [errorTask, operationTask]).ConfigureAwait(false);
        }
    }
}

/// <summary>面向协议调用方的进程连接：包装标准输入与有界按行读取器。</summary>
internal sealed class BoundedProcessConnection(StreamWriter input, BoundedUtf8LineReader output)
{
    /// <summary>异步读取一行输出；流结束时返回 null。</summary>
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => output.ReadLineAsync(cancellationToken);

    /// <summary>写出一行协议消息并立即刷新。</summary>
    public async Task WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        await input.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>输出字节预算：跨读取调用累计计量，超过上限即抛 <see cref="ProcessOutputLimitException"/>。</summary>
internal sealed class ProcessOutputBudget
{
    private readonly int maximumBytes;
    private long consumedBytes;

    public ProcessOutputBudget(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.maximumBytes = maximumBytes;
    }

    /// <summary>记录新读取的字节数并校验是否超限（线程安全，读取器可并发调用）。</summary>
    public void Consume(int count)
    {
        if (Interlocked.Add(ref consumedBytes, count) > maximumBytes)
        {
            throw new ProcessOutputLimitException(maximumBytes);
        }
    }
}

/// <summary>
/// 有界 UTF-8 按行读取器：自带 4 KB 缓冲，识别 CRLF/LF/CR 三种换行，
/// 严格 UTF-8 解码（非法字节抛异常），首行去掉 BOM；所有异常都会回调 onFailure 后原样重抛。
/// </summary>
internal sealed class BoundedUtf8LineReader(Stream stream, ProcessOutputBudget budget, Action<Exception>? onFailure = null)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly byte[] buffer = new byte[4096];
    private int position;
    private int length;
    private bool skipLineFeed;
    private bool firstLine = true;

    /// <summary>读取一行；流结束返回 null，流结束时缓冲里若有半行内容则返回该半行。</summary>
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
                    if (length == 0)
                    {
                        return line.Length == 0 ? null : Decode(line);
                    }

                    budget.Consume(length);
                }

                byte value = buffer[position++];
                if (skipLineFeed)
                {
                    // 上一字节是 CR：若本字节是 LF 则吞掉，凑成完整 CRLF
                    skipLineFeed = false;
                    if (value == '\n')
                    {
                        continue;
                    }
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

    /// <summary>读取到流结束：未提供行回调时聚合为单个字符串，否则逐行回调后返回空串。</summary>
    public async Task<string> ReadToEndAsync(CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        try
        {
            var text = onLine is null ? new StringBuilder() : null;
            while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                if (onLine is null)
                {
                    text!.AppendLine(line);
                }
                else
                {
                    onLine(line);
                }
            }

            return text?.ToString() ?? string.Empty;
        }
        catch (Exception exception)
        {
            onFailure?.Invoke(exception);
            throw;
        }
    }

    /// <summary>把一行字节按严格 UTF-8 解码；首行去掉可能存在的 BOM。</summary>
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
