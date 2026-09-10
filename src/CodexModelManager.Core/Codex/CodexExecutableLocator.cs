using System.Diagnostics;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// Codex CLI 启动命令：可执行文件、前置参数（如 npm 入口脚本）与定位来源说明。
/// </summary>
public sealed record CodexLaunchCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string Source)
{
    /// <summary>基于启动命令构造子进程启动信息（隐藏窗口、组合前置参数与业务参数）。</summary>
    public ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(FileName),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in PrefixArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

/// <summary>
/// Codex CLI 可执行定位器，按优先级依次探测：
/// CMM_CODEX_EXE 环境变量 → 用户目录安装（.codex/.sandbox-bin、plugins/.plugin-appserver）
/// → 运行中的 codex 进程 → PATH 直接命中 → PATH 上的 npm 垫片（codex.cmd/ps1）→ WindowsApps 别名。
/// </summary>
public static class CodexExecutableLocator
{
    /// <summary>仅当定位结果不含前置参数（真正的 codex.exe）时返回其路径，否则 null。</summary>
    public static string? Find()
    {
        CodexLaunchCommand? invocation = FindInvocation();
        return invocation is { PrefixArguments.Count: 0 } ? invocation.FileName : null;
    }

    /// <summary>定位 Codex CLI 启动命令（自动枚举运行中的 codex 进程）。</summary>
    public static CodexLaunchCommand? FindInvocation() => FindInvocation(null);

    /// <summary>定位启动命令；<paramref name="runningExecutablePaths"/> 提供已知运行中进程路径（测试注入用，null 则现场枚举）。</summary>
    internal static CodexLaunchCommand? FindInvocation(IEnumerable<string?>? runningExecutablePaths)
    {
        string? configured = Environment.GetEnvironmentVariable("CMM_CODEX_EXE");
        CodexLaunchCommand? configuredCommand = TryCreateInvocation(configured, "CMM_CODEX_EXE");
        if (configuredCommand is not null)
        {
            return configuredCommand;
        }

        // 用户目录可能来自 USERPROFILE 环境变量或已知文件夹 API，两者都尝试
        var profileRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? profileEnvironment = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(profileEnvironment))
        {
            profileRoots.Add(profileEnvironment);
        }

        string profileKnownFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profileKnownFolder))
        {
            profileRoots.Add(profileKnownFolder);
        }

        foreach (string profileRoot in profileRoots)
        {
            string[] userInstallCandidates =
            [
                Path.Combine(profileRoot, ".codex", ".sandbox-bin", "codex.exe"),
                Path.Combine(profileRoot, ".codex", "plugins", ".plugin-appserver", "codex.exe"),
            ];
            foreach (string candidate in userInstallCandidates)
            {
                CodexLaunchCommand? command = TryCreateInvocation(candidate, "Codex user install");
                if (command is not null)
                {
                    return command;
                }
            }
        }

        if (runningExecutablePaths is not null)
        {
            foreach (string? path in runningExecutablePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                CodexLaunchCommand? command = TryCreateInvocation(path, "running Codex process");
                if (command is not null)
                {
                    return command;
                }
            }
        }
        else
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("codex"))
                {
                    using (process)
                    {
                        try
                        {
                            CodexLaunchCommand? command = TryCreateInvocation(process.MainModule?.FileName, "running Codex process");
                            if (command is not null)
                            {
                                return command;
                            }
                        }
                        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                        {
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        string[] pathDirectories = ReadPathDirectories();
        foreach (string directory in pathDirectories)
        {
            string direct = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            // WindowsApps 里的 codex.exe 是应用执行别名，交给末尾的别名分支统一处理
            if (IsWindowsAppsAlias(direct))
            {
                continue;
            }

            CodexLaunchCommand? command = TryCreateInvocation(direct, "PATH executable");
            if (command is not null)
            {
                return command;
            }
        }

        // Windows 上 npm 全局安装是 .cmd/.ps1 垫片，需要解析出 node + 入口脚本
        if (OperatingSystem.IsWindows())
        {
            foreach (string directory in pathDirectories)
            {
                foreach (string shimName in new[] { "codex.cmd", "codex.ps1" })
                {
                    string shim = Path.Combine(directory, shimName);
                    CodexLaunchCommand? npm = TryCreateNpmInvocation(shim, pathDirectories);
                    if (npm is not null)
                    {
                        return npm;
                    }
                }
            }
        }

        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "codex.exe");
        return TryCreateInvocation(alias, "WindowsApps alias");
    }

    /// <summary>尝试把路径包装为启动命令：去引号、取全路径并确认存在；Windows 上的非 .exe 转按 npm 垫片处理。</summary>
    private static CodexLaunchCommand? TryCreateInvocation(string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(path.Trim('"'));
            if (!File.Exists(fullPath))
            {
                return null;
            }

            if (OperatingSystem.IsWindows() && !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return TryCreateNpmInvocation(fullPath, ReadPathDirectories());
            }

            return new CodexLaunchCommand(fullPath, [], source);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 npm 垫片为“node + codex.js 入口”：垫片同目录须存在
    /// node_modules/@openai/codex/bin/codex.js，node 取垫片目录或 PATH 上的 node.exe。
    /// </summary>
    internal static CodexLaunchCommand? TryCreateNpmInvocation(string shimPath, IReadOnlyList<string> pathDirectories)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(shimPath))
        {
            return null;
        }

        string? shimDirectory = Path.GetDirectoryName(Path.GetFullPath(shimPath));
        if (string.IsNullOrWhiteSpace(shimDirectory))
        {
            return null;
        }

        string entryPoint = Path.Combine(shimDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        if (!File.Exists(entryPoint))
        {
            return null;
        }

        string[] nodeCandidates =
        [
            Path.Combine(shimDirectory, "node.exe"),
            .. pathDirectories.Select(directory => Path.Combine(directory, "node.exe")),
        ];
        string? node = nodeCandidates.FirstOrDefault(File.Exists);
        return node is null ? null : new CodexLaunchCommand(Path.GetFullPath(node), [Path.GetFullPath(entryPoint)], "verified npm shim");
    }

    /// <summary>读取 PATH 环境变量并解析为去重后的目录列表（非法条目跳过）。</summary>
    private static string[] ReadPathDirectories()
    {
        List<string> directories = [];
        foreach (string rawDirectory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                directories.Add(Path.GetFullPath(rawDirectory.Trim('"')));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>判断路径是否位于 WindowsApps 应用执行别名目录下。</summary>
    private static bool IsWindowsAppsAlias(string path)
    {
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        return Path.GetFullPath(path).StartsWith(Path.GetFullPath(windowsApps).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
