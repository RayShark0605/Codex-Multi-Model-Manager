using System.Diagnostics;

namespace CodexModelManager.Core.Codex;

public static class CodexExecutableLocator
{
    public static string? Find()
    {
        string? configured = Environment.GetEnvironmentVariable("CMM_CODEX_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        // The executable inside the packaged WindowsApps directory can be readable yet
        // reject CreateProcess for an unpackaged desktop application. Codex Desktop keeps
        // runnable copies under the user's Codex home; prefer those for the opt-in smoke test.
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
                if (File.Exists(candidate)) return candidate;
            }
        }

        try
        {
            foreach (Process process in Process.GetProcessesByName("codex"))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
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

        string alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "codex.exe");
        if (File.Exists(alias)) return alias;

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string path = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "codex.exe" : "codex");
                if (File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }

        return null;
    }
}
