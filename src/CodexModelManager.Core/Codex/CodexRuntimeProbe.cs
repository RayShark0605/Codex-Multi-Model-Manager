using System.Diagnostics;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

public sealed partial class CodexRuntimeProbe : ICodexRuntimeProbe
{
    private readonly ICodexHomeProvider homeProvider;
    private readonly IConfigPatchEngine config;

    public CodexRuntimeProbe(ICodexHomeProvider homeProvider, IConfigPatchEngine config)
    {
        this.homeProvider = homeProvider;
        this.config = config;
    }

    public async Task<CodexEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        string home = homeProvider.GetCodexHome();
        string configPath = Path.Combine(home, "config.toml");
        TextFileSnapshot snapshot = await TextFileCodec.ReadAsync(configPath, cancellationToken).ConfigureAwait(false);
        ProviderKind provider = ProviderKind.Unknown;
        string? providerId = null;
        string? model = null;
        string? reasoning = null;
        string? warning = null;
        if (!snapshot.Fingerprint.Exists)
        {
            provider = ProviderKind.OpenAI;
            providerId = "openai";
            // Missing is a valid first-run state. Initial Snapshot records the
            // absence and the transactional writer can safely create the file.
        }
        else
        {
            try
            {
                ConfigReadResult read = config.Read(snapshot.Text);
                providerId = Unquote(read.RootValues.GetValueOrDefault("model_provider")) ?? "openai";
                provider = ParseProvider(providerId);
                model = Unquote(read.RootValues.GetValueOrDefault("model"));
                reasoning = Unquote(read.RootValues.GetValueOrDefault("model_reasoning_effort"));
            }
            catch (InvalidDataException exception)
            {
                warning = "config.toml 无效，写入已禁用: " + FirstLine(exception.Message);
            }
        }

        List<ProcessSnapshot> processSnapshot = CaptureProcesses();
        string[] processes = DetectProcesses(processSnapshot);
        CodexLaunchCommand? launchCommand = CodexExecutableLocator.FindInvocation(
            processSnapshot.Where(IsCodexProcess).Select(process => process.Path));
        string? executable = launchCommand?.FileName;
        var appServer = new CodexAppServerClient(home, launchCommand);
        string? cliVersion = await appServer.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        string? desktopVersion = DetectDesktopVersion(executable, processSnapshot);

        return new CodexEnvironmentInfo(
            home,
            configPath,
            desktopVersion,
            cliVersion,
            processes.Length > 0,
            processes,
            provider,
            providerId,
            model,
            reasoning,
            File.Exists(Path.Combine(home, "models.json")),
            Directory.Exists(Path.Combine(home, "backup-deepseek")),
            snapshot.Fingerprint,
            warning);
    }

    public static ProviderKind ParseProvider(string? providerId) => providerId?.ToLowerInvariant() switch
    {
        null or "" or "openai" => ProviderKind.OpenAI,
        "deepseek" => ProviderKind.DeepSeek,
        "lmstudio" or "lmstudio_local" or "lmstudio_local_cmm" => ProviderKind.LmStudio,
        _ => ProviderKind.Unknown,
    };

    public static string? Unquote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string value = raw.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            if (value[0] == '\'') return value[1..^1];
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(value); } catch (System.Text.Json.JsonException) { }
        }

        return value;
    }

    private static List<ProcessSnapshot> CaptureProcesses()
    {
        List<ProcessSnapshot> snapshots = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    string name = process.ProcessName;
                    ProcessModule? module = process.MainModule;
                    FileVersionInfo? version = module?.FileVersionInfo;
                    snapshots.Add(new ProcessSnapshot(
                        process.Id,
                        name,
                        module?.FileName,
                        version?.FileDescription,
                        version?.ProductName));
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    try
                    {
                        snapshots.Add(new ProcessSnapshot(process.Id, process.ProcessName, null, null, null));
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        return snapshots;
    }

    private static string[] DetectProcesses(IEnumerable<ProcessSnapshot> snapshots) => snapshots
        .Where(snapshot => !snapshot.Name.StartsWith("CodexModelManager", StringComparison.OrdinalIgnoreCase))
        .Where(snapshot => IsCodexProcess(snapshot) || IsChatGptProcess(snapshot))
        .Select(snapshot => $"{snapshot.Name} (PID {snapshot.Id})")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsCodexProcess(ProcessSnapshot snapshot)
    {
        string evidence = string.Join(' ', snapshot.Name, snapshot.Description, snapshot.ProductName, snapshot.Path);
        return snapshot.Name.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChatGptProcess(ProcessSnapshot snapshot)
    {
        string evidence = string.Join(' ', snapshot.Name, snapshot.Description, snapshot.ProductName, snapshot.Path);
        return snapshot.Name.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectDesktopVersion(string? executable, IEnumerable<ProcessSnapshot> processes)
    {
        string combined = executable + " " + string.Join(' ', processes.Select(process => process.Path));
        Match match = DesktopVersionRegex().Match(combined);
        if (match.Success) return match.Groups["version"].Value;
        foreach (ProcessSnapshot process in processes)
        {
            match = DesktopVersionRegex().Match(process.Path ?? string.Empty);
            if (match.Success) return match.Groups["version"].Value;
        }

        return null;
    }

    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;

    [GeneratedRegex("OpenAI\\.Codex_(?<version>[0-9]+(?:\\.[0-9]+){2,3})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesktopVersionRegex();

    private sealed record ProcessSnapshot(
        int Id,
        string Name,
        string? Path,
        string? Description,
        string? ProductName);
}
