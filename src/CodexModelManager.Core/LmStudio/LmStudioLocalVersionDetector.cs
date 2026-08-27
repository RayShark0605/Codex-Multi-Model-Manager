using System.ComponentModel;
using System.Diagnostics;

namespace CodexModelManager.Core.LmStudio;

public static class LmStudioLocalVersionDetector
{
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
