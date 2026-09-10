using System.ComponentModel;
using System.Diagnostics;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 子进程终止与清空工具：杀死进程树后，限时等待退出与输出读取任务收尾，
/// 超时则强制关闭重定向管道，保证进程清理不会无限挂起。
/// </summary>
internal static class BoundedProcessCleanup
{
    // 等待退出与读取任务收尾的默认时限
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 终止进程并等待其退出与全部 <paramref name="readerTasks"/> 完成；
    /// 超时（默认 2 秒，可用 <paramref name="maximumWait"/> 覆盖）则关闭重定向管道促使读取任务结束。
    /// </summary>
    internal static async Task TerminateAndDrainAsync(Process process, IEnumerable<Task> readerTasks, TimeSpan? maximumWait = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        TimeSpan wait = maximumWait ?? DefaultWait;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
        {
            // 进程可能已自行退出或句柄失效，此处忽略即可
        }

        Task exitTask;
        try
        {
            exitTask = process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            exitTask = Task.CompletedTask;
        }

        Task observation = Task.WhenAll([exitTask, .. readerTasks]);
        Task firstWait = Task.Delay(wait);
        if (await Task.WhenAny(observation, firstWait).ConfigureAwait(false) != observation || !observation.IsCompletedSuccessfully)
        {
            CloseRedirectedPipes(process);
        }

        try
        {
            await observation.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 清理是尽力而为，绝不能覆盖主流程的原始异常
        }
    }

    /// <summary>逐个关闭进程的标准输入/输出/错误管道（各自独立容错）。</summary>
    private static void CloseRedirectedPipes(Process process)
    {
        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.Close();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }

        try
        {
            if (process.StartInfo.RedirectStandardOutput)
            {
                process.StandardOutput.Close();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }

        try
        {
            if (process.StartInfo.RedirectStandardError)
            {
                process.StandardError.Close();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }
    }
}
