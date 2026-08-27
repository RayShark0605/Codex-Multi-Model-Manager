using System.ComponentModel;
using System.Diagnostics;

namespace CodexModelManager.Core.Infrastructure;

internal static class BoundedProcessCleanup
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    public static async Task TerminateAndDrainAsync(
        Process process,
        IEnumerable<Task> readerTasks,
        TimeSpan? maximumWait = null)
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
        if (await Task.WhenAny(observation, firstWait).ConfigureAwait(false) != observation ||
            !observation.IsCompletedSuccessfully)
        {
            CloseRedirectedPipes(process);
        }

        try
        {
            await observation.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup is best-effort and must never replace the primary process error.
        }
    }

    private static void CloseRedirectedPipes(Process process)
    {
        try
        {
            if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }

        try
        {
            if (process.StartInfo.RedirectStandardOutput) process.StandardOutput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }

        try
        {
            if (process.StartInfo.RedirectStandardError) process.StandardError.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }
    }
}
