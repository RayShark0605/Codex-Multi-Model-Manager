using System.Text.Json;

while (await Console.In.ReadLineAsync() is { } line)
{
    JsonDocument request;
    try
    {
        request = JsonDocument.Parse(line);
    }
    catch (JsonException)
    {
        continue;
    }

    using (request)
    {
        JsonElement root = request.RootElement;
        if (!root.TryGetProperty("id", out JsonElement id) || !root.TryGetProperty("method", out JsonElement methodElement)) continue;
        string? method = methodElement.GetString();
        object? result = method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "codex-model-manager-test-mcp", version = "1.0.0" },
            },
            "tools/list" => new
            {
                tools = new[]
                {
                    new
                    {
                        name = "cmm_ping",
                        description = "Harmless Codex Multi-Model Manager MCP compatibility test.",
                        inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
                    },
                },
            },
            "tools/call" => HandleCall(root),
            _ => null,
        };
        object response = result is null
            ? new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Method not found" } }
            : new { jsonrpc = "2.0", id = id.Clone(), result };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
        await Console.Out.FlushAsync();
    }
}

static object HandleCall(JsonElement root)
{
    string? name = root.TryGetProperty("params", out JsonElement parameters) && parameters.TryGetProperty("name", out JsonElement toolName)
        ? toolName.GetString()
        : null;
    return name == "cmm_ping"
        ? new { content = new[] { new { type = "text", text = "CMM_PONG" } }, structuredContent = new { value = "CMM_PONG" }, isError = false }
        : new { content = new[] { new { type = "text", text = "Unknown tool" } }, isError = true };
}
