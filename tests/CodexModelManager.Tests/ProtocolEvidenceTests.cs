using System.Text;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Tests;

public sealed class ProtocolEvidenceTests
{
    [Theory]
    [InlineData("event: error\ndata: {\"type\":\"error\",\"message\":\"failed\"}\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {")]
    [InlineData("data: {\"type\":\"response.created\"}\n")]
    [InlineData("data: {}\n\n")]
    [InlineData("data: []\n\n")]
    [InlineData("data: {\"type\":\"response.failed\"}\n\n")]
    [InlineData("event: response.created\ndata: {\"error\":{\"message\":\"failed\"}}\n\n")]
    public async Task StreamingRejectsErrorsSentinelsAndIncompleteFrames(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.False(await ResponsesSseParser.HasValidEventAsync(stream, 65_536, CancellationToken.None));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task StreamingAcceptsAnIncrementallySplitCompleteEvent(string newline)
    {
        string text = $": heartbeat{newline}{newline}event: response.output_text.delta{newline}data: {{\"type\":\"response.output_text.delta\",\"delta\":\"模型\"}}{newline}{newline}";
        using var stream = new SingleByteStream(Encoding.UTF8.GetBytes(text));
        Assert.True(await ResponsesSseParser.HasValidEventAsync(stream, 65_536, CancellationToken.None));
    }

    [Fact]
    public async Task StreamingRejectsAnOversizedPrefixWithoutUsingATruncatedEvent()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string(':', 300)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ResponsesSseParser.HasValidEventAsync(stream, 256, CancellationToken.None));
    }

    [Theory]
    [InlineData("item.started", "completed", null)]
    [InlineData("item.completed", "failed", "CMM_PONG")]
    [InlineData("item.completed", "in_progress", "CMM_PONG")]
    [InlineData("item.completed", "completed", null)]
    public void SmokeDoesNotAcceptAnUnsuccessfulOrUnprovenMcpCall(string eventType, string status, string? marker)
    {
        var evidence = new CodexSmokeEvidenceParser();
        evidence.AcceptLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = eventType,
            item = new { id = "mcp-1", type = "mcp_tool_call", server = "cmm_test", tool = "cmm_ping", status, result = new { content = new[] { new { type = "text", text = marker } } } },
        }));
        Assert.False(evidence.McpSucceeded);
    }

    [Fact]
    public void SmokeUsesTerminalEnvelopeAndToolResultsInsteadOfRecursiveNames()
    {
        var evidence = new CodexSmokeEvidenceParser();
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"text-1\",\"type\":\"agent_message\",\"text\":\"cmm_ping CMM_PONG\",\"metadata\":{\"type\":\"command_execution\",\"status\":\"completed\",\"exit_code\":0}}}");
        Assert.False(evidence.ShellSucceeded);
        Assert.False(evidence.McpSucceeded);
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"shell-1\",\"type\":\"command_execution\",\"status\":\"completed\",\"exit_code\":0}}");
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"patch-1\",\"type\":\"file_change\",\"status\":\"completed\",\"changes\":[{\"kind\":\"add\",\"path\":\"result.txt\"}]}}");
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"mcp-1\",\"type\":\"mcp_tool_call\",\"server\":\"cmm_test\",\"tool\":\"cmm_ping\",\"status\":\"completed\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"CMM_PONG\"}],\"isError\":false}}}");
        Assert.True(evidence.ShellSucceeded);
        Assert.True(evidence.PatchSucceeded);
        Assert.True(evidence.McpSucceeded);
    }

    [Fact]
    public void SmokeRejectsFailedExitErrorResultsAndConflictingTerminalEvidence()
    {
        var evidence = new CodexSmokeEvidenceParser();
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"shell-1\",\"type\":\"command_execution\",\"status\":\"completed\",\"exit_code\":1}}");
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"mcp-1\",\"type\":\"mcp_tool_call\",\"server\":\"cmm_test\",\"tool\":\"cmm_ping\",\"status\":\"completed\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"CMM_PONG\"}],\"isError\":true}}}");
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"patch-1\",\"type\":\"file_change\",\"status\":\"completed\",\"changes\":[{\"kind\":\"add\",\"path\":\"result.txt\"}]}}");
        evidence.AcceptLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"patch-1\",\"type\":\"file_change\",\"status\":\"failed\"}}");
        Assert.False(evidence.ShellSucceeded);
        Assert.False(evidence.McpSucceeded);
        Assert.False(evidence.PatchSucceeded);
    }

    private sealed class SingleByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
