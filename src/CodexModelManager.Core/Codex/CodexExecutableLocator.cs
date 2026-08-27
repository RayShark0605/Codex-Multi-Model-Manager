using System.Diagnostics;

namespace CodexModelManager.Core.Codex;

public sealed record CodexLaunchCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string Source)
{
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

public static class CodexExecutableLocator
{
    public static string? Find()
    {
        CodexLaunchCommand? invocation = FindInvocation();
        return invocation is { PrefixArguments.Count: 0 } ? invocation.FileName : null;
    }

    public static CodexLaunchCommand? FindInvocation() => FindInvocation(null);

    internal static CodexLaunchCommand? FindInvocation(IEnumerable<string?>? runningExecutablePaths)
    {
        string? configured = Environment.GetEnvironmentVariable("CMM_CODEX_EXE");
        CodexLaunchCommand? configuredCommand = TryCreateInvocation(configured, "CMM_CODEX_EXE");
        if (configuredCommand is not null)
        {
            return configuredCommand;
        }

        var profileRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? profileEnvironment = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(profileEnvironment)) profileRoots.Add(profileEnvironment);
        string profileKnownFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profileKnownFolder)) profileRoots.Add(profileKnownFolder);
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

        string alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "codex.exe");
        return TryCreateInvocation(alias, "WindowsApps alias");
    }

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
        return node is null
            ? null
            : new CodexLaunchCommand(Path.GetFullPath(node), [Path.GetFullPath(entryPoint)], "verified npm shim");
    }

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

    private static bool IsWindowsAppsAlias(string path)
    {
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        return Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(windowsApps).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
