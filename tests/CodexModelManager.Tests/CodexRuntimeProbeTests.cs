using CodexModelManager.Core.Codex;

namespace CodexModelManager.Tests;

/// <summary>CodexRuntimeProbe 进程检测相关测试集。</summary>
public sealed class CodexRuntimeProbeTests
{
    [Fact]
    public void DesktopSandboxServiceIsExcludedFromRunningDetection()
    {
        var service = new CodexRuntimeProbe.ProcessSnapshot(
            12892,
            "codex-windows-sandbox-service",
            @"C:\Program Files\WindowsApps\OpenAI.Codex_26.917.9434.0_x64__2p2nqsd0c76g0\app\resources\codex-windows-sandbox-service.exe",
            "ChatGPT",
            "ChatGPT");
        Assert.Empty(CodexRuntimeProbe.DetectProcesses([service]));
    }

    [Fact]
    public void SandboxServiceExclusionMatchesNameExactly()
    {
        var helper = new CodexRuntimeProbe.ProcessSnapshot(1, "codex-windows-sandbox-service-helper", null, null, null);
        Assert.Equal(["codex-windows-sandbox-service-helper (PID 1)"], CodexRuntimeProbe.DetectProcesses([helper]));
    }

    [Fact]
    public void RealCodexAndChatGptProcessesAreStillDetected()
    {
        var snapshots = new[]
        {
            new CodexRuntimeProbe.ProcessSnapshot(11, "codex", @"C:\Program Files\codex\codex.exe", "codex-cli", null),
            new CodexRuntimeProbe.ProcessSnapshot(12, "ChatGPT", @"C:\Program Files\ChatGPT\ChatGPT.exe", "ChatGPT", null),
        };
        Assert.Equal(["ChatGPT (PID 12)", "codex (PID 11)"], CodexRuntimeProbe.DetectProcesses(snapshots));
    }

    [Fact]
    public void ManagerOwnProcessIsExcluded()
    {
        var manager = new CodexRuntimeProbe.ProcessSnapshot(9, "CodexModelManager", @"D:\app\CodexModelManager.exe", null, null);
        Assert.Empty(CodexRuntimeProbe.DetectProcesses([manager]));
    }
}
