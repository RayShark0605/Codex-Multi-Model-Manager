using System.Diagnostics;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// Codex 运行环境探测器：定位主目录与 config.toml、解析当前 Provider/模型/推理力度、
/// 枚举运行中的 Codex/ChatGPT 进程，并查询 CLI 与桌面版版本号。
/// </summary>
public sealed partial class CodexRuntimeProbe : ICodexRuntimeProbe
{
    private readonly ICodexHomeProvider homeProvider;
    private readonly IConfigPatchEngine config;

    /// <summary>以指定的主目录解析器与配置补丁引擎构造探测器。</summary>
    public CodexRuntimeProbe(ICodexHomeProvider homeProvider, IConfigPatchEngine config)
    {
        this.homeProvider = homeProvider;
        this.config = config;
    }

    /// <summary>执行一次完整的环境探测（详见类说明）。</summary>
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
            // 配置缺失是合法的首次运行状态：初始快照会记录“缺失”，
            // 事务写入器随后可以安全地创建该文件。
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
        CodexLaunchCommand? launchCommand = CodexExecutableLocator.FindInvocation(processSnapshot.Where(IsCodexProcess).Select(process => process.Path));
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

    /// <summary>把 config.toml 里的 model_provider ID 解析为 ProviderKind。</summary>
    public static ProviderKind ParseProvider(string? providerId) => providerId?.ToLowerInvariant() switch
    {
        null or "" or "openai" => ProviderKind.OpenAI,
        "deepseek" => ProviderKind.DeepSeek,
        "lmstudio" or "lmstudio_local" or "lmstudio_local_cmm" => ProviderKind.LmStudio,
        // 官方 GLM 指南写 "ZAI"；社区教程有时用 "glm"
        "zai" or "glm" => ProviderKind.GLM,
        _ => ProviderKind.Unknown,
    };

    /// <summary>
    /// 去除 TOML 字符串引号：两侧被同类引号包裹且能按 TOML 重新解码时返回解码值，
    /// 否则原样返回去除首尾空白的结果（保持对非 TOML 输入的宽容读取契约）。
    /// </summary>
    public static string? Unquote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            try
            {
                TomlSourceDocument document = TomlSourceDocument.Parse("value = " + value);
                if (document.Tables.Count == 0 && document.Assignments.Count == 1 && document.Assignments[0].RawValue == value && document.Assignments[0].StringValue is string decoded)
                {
                    return decoded;
                }
            }
            catch (InvalidDataException)
            {
                // 非 TOML 输入：保持宽容读取契约，按原文返回
            }
        }

        return value;
    }

    /// <summary>枚举当前系统全部进程（跳过自身），尽力采集进程名、可执行路径与版本描述。</summary>
    private static List<ProcessSnapshot> CaptureProcesses()
    {
        List<ProcessSnapshot> snapshots = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    string name = process.ProcessName;
                    ProcessModule? module = process.MainModule;
                    FileVersionInfo? version = module?.FileVersionInfo;
                    snapshots.Add(new ProcessSnapshot(process.Id, name, module?.FileName, version?.FileDescription, version?.ProductName));
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

    /// <summary>筛出运行中的 Codex/ChatGPT 进程（排除本管理器自身），去重排序后格式化为“名称 (PID)”。</summary>
    private static string[] DetectProcesses(IEnumerable<ProcessSnapshot> snapshots) => snapshots
        .Where(snapshot => !snapshot.Name.StartsWith("CodexModelManager", StringComparison.OrdinalIgnoreCase))
        .Where(snapshot => IsCodexProcess(snapshot) || IsChatGptProcess(snapshot))
        .Select(snapshot => $"{snapshot.Name} (PID {snapshot.Id})")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>按进程名与“OpenAI Codex”字样判定是否 Codex 进程。</summary>
    private static bool IsCodexProcess(ProcessSnapshot snapshot)
    {
        string evidence = string.Join(' ', snapshot.Name, snapshot.Description, snapshot.ProductName, snapshot.Path);
        return snapshot.Name.Contains("codex", StringComparison.OrdinalIgnoreCase) || evidence.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按进程名与“ChatGPT”字样判定是否 ChatGPT 进程。</summary>
    private static bool IsChatGptProcess(ProcessSnapshot snapshot)
    {
        string evidence = string.Join(' ', snapshot.Name, snapshot.Description, snapshot.ProductName, snapshot.Path);
        return snapshot.Name.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) || evidence.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从可执行路径（含桌面版安装目录特征 OpenAI.Codex_x.y.z.w）提取桌面版版本号。</summary>
    private static string? DetectDesktopVersion(string? executable, IEnumerable<ProcessSnapshot> processes)
    {
        string combined = executable + " " + string.Join(' ', processes.Select(process => process.Path));
        Match match = DesktopVersionRegex().Match(combined);
        if (match.Success)
        {
            return match.Groups["version"].Value;
        }

        foreach (ProcessSnapshot process in processes)
        {
            match = DesktopVersionRegex().Match(process.Path ?? string.Empty);
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
    }

    /// <summary>取异常消息的第一行（避免多行诊断信息拼进告警）。</summary>
    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;

    // 匹配桌面版安装目录中的版本号片段（如 OpenAI.Codex_0.9.4_ ...）
    [GeneratedRegex("OpenAI\\.Codex_(?<version>[0-9]+(?:\\.[0-9]+){2,3})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesktopVersionRegex();

    /// <summary>单个进程的快照信息。</summary>
    private sealed record ProcessSnapshot(
        int Id,
        string Name,
        string? Path,
        string? Description,
        string? ProductName);
}
