using System.ComponentModel;
using System.Diagnostics;

namespace CodexModelManager.Core.LmStudio;

/// <summary>LM Studio 本地版本探测器：遍历进程，按进程名或产品名匹配 LM Studio 并读取产品版本。</summary>
public static class LmStudioLocalVersionDetector
{
    /// <summary>返回运行中的 LM Studio 产品版本；未运行或读取失败返回 null。</summary>
    public static string? Detect()
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    FileVersionInfo? versionInfo = process.MainModule?.FileVersionInfo;
                    string product = versionInfo?.ProductName ?? string.Empty;
                    if (process.ProcessName.Contains("lm studio", StringComparison.OrdinalIgnoreCase) || product.Contains("LM Studio", StringComparison.OrdinalIgnoreCase))
                    {
                        return versionInfo?.ProductVersion;
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }

        return null;
    }
}
