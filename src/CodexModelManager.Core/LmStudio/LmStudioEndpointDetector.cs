using System.Diagnostics;
using System.Text.RegularExpressions;
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
        Match match = PortPattern().Match(output ?? string.Empty);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int port) || port is < 1 or > 65535) return null;
        return port;
    }

    private static async Task<int?> QueryLmsPortAsync(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("server");
        start.ArgumentList.Add("status");
        using Process? process = Process.Start(start);
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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        string combined = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
        return process.ExitCode == 0 ? ParsePort(combined) : null;
    }

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

    [GeneratedRegex(@"(?im)\b(?:port\s+|(?:127\.0\.0\.1|localhost):)(\d{1,5})\b")]
    private static partial Regex PortPattern();
}
