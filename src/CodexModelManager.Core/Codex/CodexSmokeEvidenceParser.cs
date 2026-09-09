using System.Text.Json;

namespace CodexModelManager.Core.Codex;

internal sealed class CodexSmokeEvidenceParser
{
    private readonly Dictionary<string, Evidence> terminalItems = new(StringComparer.Ordinal);

    public bool ShellSucceeded => terminalItems.Values.Any(item => (item & Evidence.Shell) != 0);
    public bool PatchSucceeded => terminalItems.Values.Any(item => (item & Evidence.Patch) != 0);
    public bool McpSucceeded => terminalItems.Values.Any(item => (item & Evidence.Mcp) != 0);

    public void AcceptLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (GetString(root, "type") != "item.completed" || !root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object) return;
            string? id = GetString(item, "id");
            string? type = GetString(item, "type");
            if (string.IsNullOrWhiteSpace(id) || type is not ("command_execution" or "file_change" or "mcp_tool_call")) return;
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
            if (terminalItems.TryGetValue(id, out Evidence previous) && previous != evidence) evidence = Evidence.Conflicted;
            terminalItems[id] = evidence;
        }
        catch (JsonException)
        {
            // Non-JSON diagnostic lines never establish successful tool execution.
        }
    }

    private static bool HasExpectedFileChange(JsonElement item) =>
        item.TryGetProperty("changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array &&
        changes.EnumerateArray().Any(change =>
            GetString(change, "kind") is "add" or "update" &&
            GetString(change, "path")?.Replace('\\', '/').Split('/').LastOrDefault() == "result.txt");

    private static bool HasPingResult(JsonElement item)
    {
        if (!item.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object || HasError(result)) return false;
        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind != JsonValueKind.False) return false;
        if (result.TryGetProperty("structuredContent", out JsonElement structured) && GetString(structured, "value") == "CMM_PONG") return true;
        return result.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array &&
            content.EnumerateArray().Any(value => GetString(value, "type") == "text" && GetString(value, "text")?.Trim() == "CMM_PONG");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasError(JsonElement element) => element.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null;

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
