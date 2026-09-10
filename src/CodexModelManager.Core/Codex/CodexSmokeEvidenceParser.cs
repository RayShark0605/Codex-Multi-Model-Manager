using System.Text.Json;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// 冒烟测试证据解析器：逐行解析 Codex app-server 输出的 JSON 事件流，
/// 从 item.completed 事件里归集三类“真实执行过”的证据（Shell 命令、文件写入、MCP 工具调用）。
/// </summary>
internal sealed class CodexSmokeEvidenceParser
{
    // 按条目 ID 归档最终证据；同一 ID 出现冲突结论时标记 Conflicted
    private readonly Dictionary<string, Evidence> terminalItems = new(StringComparer.Ordinal);

    /// <summary>是否存在成功的 Shell 命令执行证据。</summary>
    public bool ShellSucceeded => terminalItems.Values.Any(item => (item & Evidence.Shell) != 0);

    /// <summary>是否存在成功的文件写入证据。</summary>
    public bool PatchSucceeded => terminalItems.Values.Any(item => (item & Evidence.Patch) != 0);

    /// <summary>是否存在成功的 MCP 工具调用证据。</summary>
    public bool McpSucceeded => terminalItems.Values.Any(item => (item & Evidence.Mcp) != 0);

    /// <summary>
    /// 接收一行输出：只认 item.completed 且状态 completed、无 error 的
    /// command_execution / file_change / mcp_tool_call 条目；非 JSON 行一律忽略。
    /// </summary>
    public void AcceptLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (GetString(root, "type") != "item.completed" || !root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string? id = GetString(item, "id");
            string? type = GetString(item, "type");
            if (string.IsNullOrWhiteSpace(id) || type is not ("command_execution" or "file_change" or "mcp_tool_call"))
            {
                return;
            }

            Evidence evidence = Evidence.None;
            if (GetString(item, "status") == "completed" && !HasError(item))
            {
                evidence = type switch
                {
                    "command_execution" when item.TryGetProperty("exit_code", out JsonElement exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int code) && code == 0 => Evidence.Shell,
                    "file_change" when HasExpectedFileChange(item) => Evidence.Patch,
                    "mcp_tool_call" when GetString(item, "server") == "cmm_test" && GetString(item, "tool") == "cmm_ping" && HasPingResult(item) => Evidence.Mcp,
                    _ => Evidence.None,
                };
            }

            // 同一 ID 的最新结论与先前不一致时标记冲突，防止被覆盖造假
            if (terminalItems.TryGetValue(id, out Evidence previous) && previous != evidence)
            {
                evidence = Evidence.Conflicted;
            }

            terminalItems[id] = evidence;
        }
        catch (JsonException)
        {
            // 非 JSON 的诊断输出行永远不能作为“工具执行成功”的证据
        }
    }

    /// <summary>判断 file_change 条目是否包含对 result.txt 的 add/update 变更（路径分隔符归一化后比较）。</summary>
    private static bool HasExpectedFileChange(JsonElement item) =>
        item.TryGetProperty("changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array &&
        changes.EnumerateArray().Any(change =>
            GetString(change, "kind") is "add" or "update" &&
            GetString(change, "path")?.Replace('\\', '/').Split('/').LastOrDefault() == "result.txt");

    /// <summary>判断 MCP 条目的结果是否为有效的 CMM_PONG 应答（结构化 value 或文本内容均可）。</summary>
    private static bool HasPingResult(JsonElement item)
    {
        if (!item.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object || HasError(result))
        {
            return false;
        }

        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        if (result.TryGetProperty("structuredContent", out JsonElement structured) && GetString(structured, "value") == "CMM_PONG")
        {
            return true;
        }

        return result.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array &&
            content.EnumerateArray().Any(value => GetString(value, "type") == "text" && GetString(value, "text")?.Trim() == "CMM_PONG");
    }

    /// <summary>读取字符串属性；不存在或类型不符返回 null。</summary>
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>判断元素是否带有非 null 的 error 属性。</summary>
    private static bool HasError(JsonElement element) => element.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null;

    /// <summary>证据位标记：Shell / 文件写入 / MCP 调用，以及冲突标记。</summary>
    [Flags]
    private enum Evidence
    {
        None = 0,
        Shell = 1,
        Patch = 2,
        Mcp = 4,
        Conflicted = 8,
    }
}
