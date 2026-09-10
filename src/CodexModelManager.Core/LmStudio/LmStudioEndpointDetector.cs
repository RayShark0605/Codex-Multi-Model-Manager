using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// LM Studio 端点探测器：优先询问 lms CLI 的实际监听端口；
/// 拿不到时回退到 appsettings 里的配置端点，最后落到默认 127.0.0.1:1234。
/// </summary>
public sealed partial class LmStudioEndpointDetector
{
    /// <summary>按上述优先级探测端点。</summary>
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

    /// <summary>从 lms 输出文本中解析端口号：收集全部合法端口，恰好唯一时返回，否则 null。</summary>
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

    /// <summary>运行“lms server status”并解析监听端口；失败或超时返回 null。</summary>
    private static async Task<int?> QueryLmsPortAsync(string executable, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = CreateLmsStatusStartInfo(executable);
        start.ArgumentList.Add("server");
        start.ArgumentList.Add("status");
        try
        {
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(start, TimeSpan.FromSeconds(4), BoundedProcessRunner.StatusOutputLimit, BoundedProcessRunner.StatusOutputLimit, cancellationToken, combineOutputBudget: true).ConfigureAwait(false);
            return result.ExitCode == 0 ? ParsePort(result.StandardOutput + "\n" + result.StandardError) : null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>构造 lms 子进程启动信息（隐藏窗口、UTF-8 重定向）。</summary>
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

    /// <summary>定位 lms 可执行文件：先看 ~/.lmstudio/bin，再扫 PATH；找不到返回 null。</summary>
    private static string? FindLmsExecutable()
    {
        string? profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(profile))
        {
            string installed = Path.Combine(profile, ".lmstudio", "bin", OperatingSystem.IsWindows() ? "lms.exe" : "lms");
            if (File.Exists(installed))
            {
                return installed;
            }
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "lms.exe" : "lms");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }

        return null;
    }

    // 匹配三种端口写法："port is/at N"、"host:N"、"port": N（JSON）
    [GeneratedRegex("(?im)(?:\\bport\\s*(?:(?:is|at)\\s+|[:=]\\s*|\\s+)(?<port>\\d{1,5})\\b|(?:https?://)?(?:127\\.0\\.0\\.1|localhost|\\[::1\\]):(?<port>\\d{1,5})\\b|\"port\"\\s*:\\s*(?<port>\\d{1,5})\\b)", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();
}
