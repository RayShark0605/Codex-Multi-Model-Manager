using System.Text.Json;

if (args.Contains("--process-fixture-hold-pipes", StringComparer.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(4));
    return;
}

if (args.Contains("--process-fixture-inherited-pipes", StringComparer.Ordinal))
{
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    }
    start.ArgumentList.Add("--process-fixture-hold-pipes");
    using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法启动有界 pipe fixture。");
    child.StandardInput.Close();
    Console.WriteLine("codex-cli 1.2.3");
    return;
}

if (args.Contains("--process-fixture-large-stdout", StringComparer.Ordinal) || args.Contains("--process-fixture-large-stderr", StringComparer.Ordinal))
{
    TextWriter output = args.Contains("--process-fixture-large-stderr", StringComparer.Ordinal) ? Console.Error : Console.Out;
    await output.WriteAsync(new string('x', 512 * 1024));
    await output.FlushAsync();
    return;
}

if (args.Contains("--process-fixture-combined-output", StringComparer.Ordinal))
{
    await Console.Out.WriteAsync(new string('x', 192 * 1024));
    await Console.Out.FlushAsync();
    await Console.Error.WriteAsync(new string('y', 192 * 1024));
    await Console.Error.FlushAsync();
    return;
}

if (args.Contains("--emit-utf8-fixture", StringComparer.Ordinal))
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    await Console.Out.WriteLineAsync(@"C:\用户\模型.gguf");
    return;
}

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
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out JsonElement id) ||
            !root.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            continue;
        }

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
    string? name = root.TryGetProperty("params", out JsonElement parameters) &&
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("name", out JsonElement toolName) &&
        toolName.ValueKind == JsonValueKind.String
        ? toolName.GetString()
        : null;
    return name == "cmm_ping"
        ? new { content = new[] { new { type = "text", text = "CMM_PONG" } }, structuredContent = new { value = "CMM_PONG" }, isError = false }
        : new { content = new[] { new { type = "text", text = "Unknown tool" } }, isError = true };
}
