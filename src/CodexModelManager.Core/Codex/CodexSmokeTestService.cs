using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;
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
        var evidence = new CodexSmokeEvidenceParser();
        BoundedProcessResult execution;
        try
        {
            execution = await BoundedProcessRunner.RunAsync(
                start, TimeSpan.FromMinutes(5), BoundedProcessRunner.CatalogOutputLimit, BoundedProcessRunner.SmokeErrorOutputLimit,
                cancellationToken, evidence.AcceptLine).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedExecution(root, "测试在 5 分钟后超时，子进程清理已完成。");
        }
        catch (ProcessOutputLimitException)
        {
            return FailedExecution(root, "测试输出超过允许上限；未将截断日志作为成功证据。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool shell = evidence.ShellSucceeded;
        bool patch = evidence.PatchSucceeded;
        bool mcp = evidence.McpSucceeded;
        bool file = await HasExpectedResultFileAsync(Path.Combine(workspace, "result.txt"), cancellationToken).ConfigureAwait(false);
        bool passed = execution.ExitCode == 0 && shell && patch && mcp && file;
        DateTimeOffset now = DateTimeOffset.Now;
        List<CompatibilityResult> results =
        [
            new("Codex Agent", passed ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, passed ? "真实 Codex CLI 在隔离工作区完成全部必需步骤。" : "未完成全部 shell/file/apply_patch/MCP 步骤。", now),
            new("Shell", shell ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, shell ? "检测到成功完成且 exit code 为 0 的 shell 调用。" : "未检测到成功完成的 shell 事件。", now),
            new("File Editing", file ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, file ? "临时 result.txt 内容正确。" : "未生成预期文件。", now),
            new("Apply Patch", patch ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, patch ? "检测到 result.txt 的成功 file_change 事件。" : "未检测到 result.txt 的成功 file_change 事件。", now),
            new("MCP", mcp ? CompatibilityStatus.Supported : CompatibilityStatus.Failed, mcp ? "临时 cmm_ping MCP 成功返回 CMM_PONG。" : "未检测到成功的 cmm_ping/CMM_PONG 结果。", now),
            new("Plan", CompatibilityStatus.Untested, "本次 Level 3 不进入 Plan Mode。", now),
            new("Goal", CompatibilityStatus.Untested, "本次 Level 3 不创建用户 Goal。", now),
        ];
        string summary = passed ? "Codex Agent Level 3 通过。" : $"Codex Agent Level 3 未通过（exit {execution.ExitCode}，stderr 类型: {ClassifyError(execution.StandardError)}）。";
        return new SmokeTestResult(passed, root, execution.ExitCode, results, summary);
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
        else if (request.TargetProvider == ProviderKind.GLM)
        {
            GlmPlatform platform = request.GlmPlatform ?? throw new InvalidOperationException("GLM Level 3 测试缺少平台选择。");
            builder.Append("model_provider = ").AppendLine(JsonSerializer.Serialize(GlmPlatforms.ProviderId));
            if (!string.IsNullOrWhiteSpace(request.GlmCatalogPath)) builder.Append("model_catalog_json = ").AppendLine(JsonSerializer.Serialize(Path.GetFullPath(request.GlmCatalogPath)));
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) builder.Append("model_reasoning_effort = ").AppendLine(JsonSerializer.Serialize(request.ReasoningEffort));
            builder.AppendLine().Append('[').Append(GlmPlatforms.ProviderTableName).AppendLine("]").Append("name = ").AppendLine(JsonSerializer.Serialize(GlmPlatforms.ProviderId)).Append("base_url = ").AppendLine(JsonSerializer.Serialize(GlmPlatforms.BaseUrl(platform))).AppendLine("wire_api = \"responses\"");
            builder.AppendLine().Append('[').Append(GlmPlatforms.ProviderTableName).AppendLine(".auth]").Append("command = ").AppendLine(JsonSerializer.Serialize(Path.GetFullPath(credentialHelperPath))).Append("args = [").Append(JsonSerializer.Serialize(CredentialNames.Glm)).AppendLine("]");
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

    private static SmokeTestResult FailedExecution(string root, string reason)
    {
        DateTimeOffset stoppedAt = DateTimeOffset.Now;
        CompatibilityResult[] results =
        [
            new("Codex Agent", CompatibilityStatus.Failed, reason, stoppedAt),
            new("Shell", CompatibilityStatus.Failed, reason, stoppedAt),
            new("File Editing", CompatibilityStatus.Failed, reason, stoppedAt),
            new("Apply Patch", CompatibilityStatus.Failed, reason, stoppedAt),
            new("MCP", CompatibilityStatus.Failed, reason, stoppedAt),
            new("Plan", CompatibilityStatus.Untested, "本次 Level 3 未验证 Plan。", stoppedAt),
            new("Goal", CompatibilityStatus.Untested, "本次 Level 3 未验证 Goal。", stoppedAt),
        ];
        return new SmokeTestResult(false, root, -1, results, reason);
    }

    private static async Task<bool> HasExpectedResultFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var reader = new BoundedUtf8LineReader(stream, new ProcessOutputBudget(1024));
            return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim() == "CMM_SMOKE_OK";
        }
        catch (ProcessOutputLimitException) { return false; }
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
