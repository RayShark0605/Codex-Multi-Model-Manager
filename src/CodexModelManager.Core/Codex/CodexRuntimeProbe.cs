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

        string[] processes = DetectProcesses();
        string? executable = CodexExecutableLocator.Find();
        var appServer = new CodexAppServerClient(home, executable);
        string? cliVersion = await appServer.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        string? desktopVersion = DetectDesktopVersion(executable, processes);

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

    private static string[] DetectProcesses()
    {
        List<string> matches = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    string name = process.ProcessName;
                    if (name.StartsWith("CodexModelManager", StringComparison.OrdinalIgnoreCase)) continue;
                    string description = process.MainModule?.FileVersionInfo.FileDescription ?? string.Empty;
                    string product = process.MainModule?.FileVersionInfo.ProductName ?? string.Empty;
                    string? path = process.MainModule?.FileName;
                    string evidence = string.Join(' ', name, description, product, path);
                    if (name.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
                        evidence.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase) ||
                        evidence.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add($"{name} (PID {process.Id})");
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    string name = process.ProcessName;
                    if (name.Contains("codex", StringComparison.OrdinalIgnoreCase) || name.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add($"{name} (PID {process.Id})");
                    }
                }
            }
        }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? DetectDesktopVersion(string? executable, IReadOnlyList<string> processes)
    {
        string combined = executable + " " + string.Join(' ', processes);
        Match match = DesktopVersionRegex().Match(combined);
        if (match.Success) return match.Groups["version"].Value;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string path = process.MainModule?.FileName ?? string.Empty;
                    match = DesktopVersionRegex().Match(path);
                    if (match.Success) return match.Groups["version"].Value;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }

        return null;
    }

    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;

    [GeneratedRegex("OpenAI\\.Codex_(?<version>[0-9]+(?:\\.[0-9]+){2,3})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesktopVersionRegex();
}
