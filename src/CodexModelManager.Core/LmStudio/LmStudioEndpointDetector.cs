using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed partial class LmStudioEndpointDetector
{
    public static async Task<LmStudioEndpointDetection> DetectAsync(Uri? configuredEndpoint = null, CancellationToken cancellationToken = default)
    {
        string? executable = FindLmsExecutable();
        if (executable is not null)
        {
            int? port = await QueryLmsPortAsync(executable, cancellationToken).ConfigureAwait(false);
            if (port is not null)
            {
                return new LmStudioEndpointDetection(new Uri($"http://127.0.0.1:{port.Value}"), "lms server status");
            }
        }

        if (configuredEndpoint is not null)
        {
            return new LmStudioEndpointDetection(configuredEndpoint, "appsettings.json");
        }

        return new LmStudioEndpointDetection(new Uri("http://127.0.0.1:1234"), "default 1234");
    }

    public static int? ParsePort(string output)
    {
        HashSet<int> ports = [];
        foreach (Match match in PortPattern().Matches(output ?? string.Empty))
        {
            foreach (Capture capture in match.Groups["port"].Captures)
            {
                if (int.TryParse(capture.Value, out int port) && port is >= 1 and <= 65_535)
                {
                    ports.Add(port);
                }
            }
        }

        return ports.Count == 1 ? ports.Single() : null;
    }

    private static async Task<int?> QueryLmsPortAsync(string executable, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = CreateLmsStatusStartInfo(executable);
        start.ArgumentList.Add("server");
        start.ArgumentList.Add("status");
        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }

        using Process? process = started;
        if (process is null) return null;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await BoundedProcessCleanup.TerminateAndDrainAsync(process, [stdout, stderr]).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        string combined = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
        return process.ExitCode == 0 ? ParsePort(combined) : null;
    }

    internal static ProcessStartInfo CreateLmsStatusStartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardInputEncoding = Encoding.UTF8,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static string? FindLmsExecutable()
    {
        string? profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(profile))
        {
            string installed = Path.Combine(profile, ".lmstudio", "bin", OperatingSystem.IsWindows() ? "lms.exe" : "lms");
            if (File.Exists(installed)) return installed;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "lms.exe" : "lms");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }

        return null;
    }

    [GeneratedRegex("(?im)(?:\\bport\\s*(?:(?:is|at)\\s+|[:=]\\s*|\\s+)(?<port>\\d{1,5})\\b|(?:https?://)?(?:127\\.0\\.0\\.1|localhost|\\[::1\\]):(?<port>\\d{1,5})\\b|\"port\"\\s*:\\s*(?<port>\\d{1,5})\\b)", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();
}
