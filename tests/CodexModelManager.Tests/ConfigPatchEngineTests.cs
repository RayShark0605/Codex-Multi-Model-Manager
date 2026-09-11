using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

/// <summary>ConfigPatchEngine 相关测试集。</summary>
public sealed class ConfigPatchEngineTests
{
    private readonly TomlConfigPatchEngine engine = new();

    [Fact]
    public void PatchPreservesCommentsMcpProjectsPermissionsAndUnknownSections()
    {
        string original = SwitchHarness.BaseConfig + "\n[future.unknown]\nmagic = { value = 7 } # keep\n";
        ConfigPatchResult result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"qwen/local\"", ["model_provider"] = "\"lmstudio\"" },
            new Dictionary<string, string?>()));
        string normalized = result.Text.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("# user comment must survive", normalized, StringComparison.Ordinal);
        Assert.Contains("model = \"qwen/local\" # inline", normalized, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.demo]\ncommand = \"demo.exe\"", normalized, StringComparison.Ordinal);
        Assert.Contains("[projects.'C:\\work']\ntrust_level = \"trusted\"", normalized, StringComparison.Ordinal);
        Assert.Contains("[permissions.safe]\nnetwork = false", normalized, StringComparison.Ordinal);
        Assert.Contains("[future.unknown]\nmagic = { value = 7 } # keep", normalized, StringComparison.Ordinal);
        Assert.Equal(1, result.Preservation.McpServerCount);
        Assert.Equal(1, result.Preservation.ProjectCount);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void PatchPreservesNewlineStyle(string newline)
    {
        string original = $"model = \"old\"{newline}{newline}[mcp_servers.x]{newline}command = \"x\"{newline}";
        string result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"new\"" }, new Dictionary<string, string?>())).Text;
        if (newline == "\r\n")
        {
            Assert.DoesNotContain("\n", result.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.EndsWith(newline, result, StringComparison.Ordinal);
    }

    [Fact]
    public void TextCodecPreservesUtf8Bom()
    {
        string text = "model = \"old\"\n";
        byte[] bytes = TextFileCodec.Encode(text, new CodexModelManager.Core.Models.TextFileFormat(true, "\n", true, false));
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void EmptyConfigCanReceiveManagedKeys()
    {
        ConfigPatchResult result = engine.Apply(string.Empty, new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"gpt-5.6-sol\"" }, new Dictionary<string, string?>()));
        engine.Validate(result.Text);
        Assert.Contains("model = \"gpt-5.6-sol\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NonSecretProviderTableMutationShowsRealBody()
    {
        string body = "name = \"LM Studio Local\"\nbase_url = \"http://127.0.0.1:1234/v1\"\nwire_api = \"responses\"\n" + LmStudioStreamResilience.TableBody;
        ConfigPatchResult result = engine.Apply("model = \"old\"\n", new ConfigPatchRequest(
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["model_providers.lmstudio_local_cmm"] = body }));

        ConfigMutation mutation = Assert.Single(result.Mutations);
        Assert.Equal("[model_providers.lmstudio_local_cmm]", mutation.KeyPath);
        Assert.Null(mutation.OldValue);
        Assert.Equal(body, mutation.NewValue);
        Assert.False(mutation.IsSecret);
    }

    [Fact]
    public void SecretProviderTableMutationStaysMasked()
    {
        const string secretBody = "name = \"ZAI\"\nbase_url = \"https://open.bigmodel.cn/api/v1\"\nexperimental_bearer_token = \"sk-fixture-secret\"";
        ConfigPatchResult result = engine.Apply("model = \"old\"\n", new ConfigPatchRequest(
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["model_providers.ZAI"] = secretBody }));

        ConfigMutation mutation = Assert.Single(result.Mutations);
        Assert.Equal("<managed table>", mutation.NewValue);
        Assert.True(mutation.IsSecret);
        Assert.DoesNotContain("sk-fixture-secret", mutation.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptedConfigIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => engine.Validate("model = [\n"));
    }

    [Fact]
    public void DuplicateTomlKeyIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => engine.Validate("model = \"a\"\nmodel = \"b\"\n"));
    }

    [Fact]
    public void UnregisteredKeyCannotBeModified()
    {
        Assert.Throws<InvalidOperationException>(() => engine.Apply("x = 1\n", new ConfigPatchRequest(new Dictionary<string, string?> { ["sandbox_mode"] = "\"danger-full-access\"" }, new Dictionary<string, string?>())));
    }

    [Fact]
    public void ManagedProviderTableIsReplacedWithoutTouchingNeighbors()
    {
        string original = "model = \"x\"\n\n[model_providers.deepseek]\nname = \"old\"\n\n[mcp_servers.keep]\ncommand = \"keep\"\n";
        string result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?>(), new Dictionary<string, string?> { ["model_providers.deepseek"] = "name = \"new\"\nbase_url = \"https://example/\"" })).Text;
        Assert.Contains("name = \"new\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("name = \"old\"", result, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.keep]\ncommand = \"keep\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedTableRemovalStopsAtUnknownArrayTable()
    {
        const string arraySection = "[[future.workers]]\nname = \"first\"\n\n[[future.workers]]\nname = \"second\"\n";
        string original = "model = \"x\"\n\n[model_providers.deepseek]\nname = \"old\"\n\n" + arraySection;

        string result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?> { ["model_provider"] = "\"openai\"" },
            new Dictionary<string, string?> { ["model_providers.deepseek"] = null },
            ["model_providers.deepseek"])).Text;

        Assert.DoesNotContain("name = \"old\"", result, StringComparison.Ordinal);
        Assert.Contains(arraySection, result, StringComparison.Ordinal);
        engine.Validate(result);
    }

    [Fact]
    public void ProjectCountTreatsDotsInsideQuotedPathsAsPartOfOneKey()
    {
        const string original = "model = \"x\"\n\n[projects.'C:\\work.one']\ntrust_level = \"trusted\"\n\n[projects.'C:\\work.two']\ntrust_level = \"trusted\"\n";

        ConfigPatchResult result = engine.Apply(original, new ConfigPatchRequest(new Dictionary<string, string?> { ["model"] = "\"y\"" },
            new Dictionary<string, string?>()));

        Assert.Equal(2, result.Preservation.ProjectCount);
        Assert.Contains("[projects.'C:\\work.one']", result.Text, StringComparison.Ordinal);
        Assert.Contains("[projects.'C:\\work.two']", result.Text, StringComparison.Ordinal);
    }
}
