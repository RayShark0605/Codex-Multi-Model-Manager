using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Core.Codex;

public sealed class CodexSmokeTestService
{
    private readonly string credentialHelperPath;
    private readonly string mcpServerPath;

    public CodexSmokeTestService(string credentialHelperPath, string mcpServerPath)
    {
        this.credentialHelperPath = credentialHelperPath;
        this.mcpServerPath = mcpServerPath;
    }

    public async Task<SmokeTestResult> RunAsync(SwitchRequest request, CancellationToken cancellationToken = default)
    {
        CodexLaunchCommand? codex = CodexExecutableLocator.FindInvocation();
        if (codex is null) throw new InvalidOperationException("未找到可安全启动的 Codex CLI。");
        if (!File.Exists(mcpServerPath)) throw new FileNotFoundException("临时 MCP 测试服务器不存在。", mcpServerPath);
        if (request.TargetProvider == ProviderKind.OpenAI)
        {
            throw new InvalidOperationException("OpenAI Level 3 需要账户凭据；测试不会复制或读取 auth.json。请在 Codex 原生客户端验证。");
        }

        string root = Path.Combine(Path.GetTempPath(), "CodexModelManager", "smoke", Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "input.txt"), "CMM_INPUT_OK\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), BuildConfig(request), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        string prompt = "This is a harmless compatibility test in an isolated temporary directory. Read input.txt. Use the shell tool to run PowerShell Get-Content on input.txt. Call MCP tool cmm_ping. Use apply_patch to create result.txt containing exactly CMM_SMOKE_OK and a newline. Do not access paths outside the current workspace.";
        ProcessStartInfo start = codex.CreateStartInfo([]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.WorkingDirectory = workspace;
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("approval_policy=\"never\"");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("sandbox_mode=\"workspace-write\"");
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--skip-git-repo-check");
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(workspace);
        start.ArgumentList.Add(prompt);
        start.Environment["CODEX_HOME"] = home;

        start.RedirectStandardInput = true;
        start.StandardInputEncoding = Encoding.UTF8;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Codex CLI smoke test。");
        process.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await BoundedProcessCleanup.TerminateAndDrainAsync(process, [stdoutTask, stderrTask]).ConfigureAwait(false);
            DateTimeOffset stoppedAt = DateTimeOffset.Now;
            string reason = cancellationToken.IsCancellationRequested ? "测试已取消。" : "测试在 5 分钟后超时，进程树已终止。";
            CompatibilityResult[] stoppedResults =
            [
                new("Codex Agent", CompatibilityStatus.Failed, reason, stoppedAt),
                new("Shell", CompatibilityStatus.Failed, reason, stoppedAt),
                new("File Editing", CompatibilityStatus.Failed, reason, stoppedAt),
                new("Apply Patch", CompatibilityStatus.Failed, reason, stoppedAt),
                new("MCP", CompatibilityStatus.Failed, reason, stoppedAt),
                new("Plan", CompatibilityStatus.Untested, "本次 Level 3 未验证 Plan。", stoppedAt),
                new("Goal", CompatibilityStatus.Untested, "本次 Level 3 未验证 Goal。", stoppedAt),
            ];
            return new SmokeTestResult(false, root, -1, stoppedResults, reason);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        bool shell = HasTypedEvent(stdout, "command_execution", "shell", "shell_command", "exec_command") || HasNamedTool(stdout, "exec_command", "shell_command");
        bool patch = HasTypedEvent(stdout, "file_change", "apply_patch") || HasNamedTool(stdout, "apply_patch");
        bool mcp = HasNamedTool(stdout, "cmm_ping") || (HasTypedEvent(stdout, "mcp_tool_call") && stdout.Contains("CMM_PONG", StringComparison.Ordinal));
        bool file = File.Exists(Path.Combine(workspace, "result.txt")) && (await File.ReadAllTextAsync(Path.Combine(workspace, "result.txt"), cancellationToken).ConfigureAwait(false)).Trim() == "CMM_SMOKE_OK";
        bool passed = process.ExitCode == 0 && shell && patch && mcp && file;
        DateTimeOffset now = DateTimeOffset.Now;
        List<CompatibilityResult> results =
        [
            new("Codex Agent", passed ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, passed ? "真实 Codex CLI 在隔离工作区完成全部必需步骤。" : "未完成全部 shell/file/apply_patch/MCP 步骤。", now),
            new("Shell", shell ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, shell ? "检测到安全 shell 调用。" : "未检测到 shell 事件。", now),
            new("File Editing", file ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, file ? "临时 result.txt 内容正确。" : "未生成预期文件。", now),
            new("Apply Patch", patch ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, patch ? "检测到 apply_patch 工具事件。" : "未检测到 apply_patch 工具事件。", now),
            new("MCP", mcp ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, mcp ? "临时 cmm_ping MCP 被调用。" : "未检测到 cmm_ping。", now),
            new("Plan", CompatibilityStatus.Untested, "本次 Level 3 不进入 Plan Mode。", now),
            new("Goal", CompatibilityStatus.Untested, "本次 Level 3 不创建用户 Goal。", now),
        ];
        string summary = passed ? "Codex Agent Level 3 通过。" : $"Codex Agent Level 3 未通过（exit {process.ExitCode}，stderr 类型: {ClassifyError(stderr)}）。";
        return new SmokeTestResult(passed, root, process.ExitCode, results, summary);
    }

    private string BuildConfig(SwitchRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("model = ").AppendLine(JsonSerializer.Serialize(request.TargetModel));
        if (request.TargetProvider == ProviderKind.DeepSeek)
        {
            builder.AppendLine("model_provider = \"deepseek\"");
            builder.AppendLine("forced_login_method = \"api\"");
            if (!string.IsNullOrWhiteSpace(request.DeepSeekCatalogPath)) builder.Append("model_catalog_json = ").AppendLine(JsonSerializer.Serialize(Path.GetFullPath(request.DeepSeekCatalogPath)));
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) builder.Append("model_reasoning_effort = ").AppendLine(JsonSerializer.Serialize(request.ReasoningEffort));
            builder.AppendLine().AppendLine("[model_providers.deepseek]").AppendLine("name = \"deepseek\"").AppendLine("base_url = \"https://api.deepseek.com/\"").AppendLine("wire_api = \"responses\"");
            builder.AppendLine().AppendLine("[model_providers.deepseek.auth]").Append("command = ").AppendLine(JsonSerializer.Serialize(Path.GetFullPath(credentialHelperPath))).Append("args = [").Append(JsonSerializer.Serialize(CredentialNames.DeepSeek)).AppendLine("]");
        }
        else
        {
            string provider = request.LmStudioProviderId ?? "lmstudio";
            builder.Append("model_provider = ").AppendLine(JsonSerializer.Serialize(provider));
            if (request.ContextWindow is int context) builder.Append("model_context_window = ").AppendLine(context.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (request.AutoCompactTokenLimit is int compact) builder.Append("model_auto_compact_token_limit = ").AppendLine(compact.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (request.AutoCompactTokenLimit is not null) builder.AppendLine("model_auto_compact_token_limit_scope = \"total\"");
            if (request.ToolOutputTokenLimit is int toolOutput) builder.Append("tool_output_token_limit = ").AppendLine(toolOutput.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (provider != "lmstudio")
            {
                Uri endpoint = request.LmStudioEndpoint ?? new Uri("http://127.0.0.1:1234");
                if (!endpoint.AbsoluteUri.EndsWith('/')) endpoint = new Uri(endpoint.AbsoluteUri + "/");
                string table = "model_providers." + provider;
                builder.AppendLine().Append('[').Append(table).AppendLine("]").AppendLine("name = \"LM Studio Local\"").Append("base_url = ").AppendLine(JsonSerializer.Serialize(new Uri(endpoint, "v1").AbsoluteUri.TrimEnd('/'))).AppendLine("wire_api = \"responses\"");
                if (request.LmStudioRequiresAuthentication)
                {
                    builder.AppendLine().Append('[').Append(table).AppendLine(".auth]").Append("command = ").AppendLine(JsonSerializer.Serialize(Path.GetFullPath(credentialHelperPath))).Append("args = [").Append(JsonSerializer.Serialize(CredentialNames.LmStudio)).AppendLine("]");
                }
            }
        }

        builder.AppendLine().AppendLine("[mcp_servers.cmm_test]").Append("command = ").AppendLine(JsonSerializer.Serialize(Path.GetFullPath(mcpServerPath)));
        return builder.ToString();
    }

    private static bool HasTypedEvent(string output, params string[] expected) => HasStructuredValue(output, "type", expected);

    private static bool HasNamedTool(string output, params string[] expected) =>
        HasStructuredValue(output, "name", expected) ||
        HasStructuredValue(output, "tool", expected) ||
        HasStructuredValue(output, "tool_name", expected);

    private static bool HasStructuredValue(string output, string propertyName, IReadOnlyCollection<string> expected)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (ContainsPropertyValue(document.RootElement, propertyName, expected)) return true;
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static bool ContainsPropertyValue(JsonElement element, string propertyName, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String &&
                    expected.Contains(property.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)) return true;
                if (ContainsPropertyValue(property.Value, propertyName, expected)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (ContainsPropertyValue(item, propertyName, expected)) return true;
            }
        }

        return false;
    }

    private static string ClassifyError(string value)
    {
        if (value.Contains("System message must be at the beginning", StringComparison.OrdinalIgnoreCase)) return "lmstudio-chat-template";
        if (value.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("status 401", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)) return "authentication";
        if (value.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "timeout";
        return string.IsNullOrWhiteSpace(value) ? "none" : "runtime";
    }
}
