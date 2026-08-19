using CodexModelManager.Core.Backup;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Tests;

public sealed class SecurityAndScannerTests
{
    [Fact]
    public void SecretRedactorCoversKnownBearerTomlQueryAndSkTokens()
    {
        var redactor = new SecretRedactor();
        redactor.Register("known-secret-value");
        string input = "Authorization: Bearer abcdef experimental_bearer_token = \"hidden\" https://x/?api_key=query sk-abcdefghijk known-secret-value";
        string result = redactor.Redact(input);
        Assert.DoesNotContain("abcdef", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", result, StringComparison.Ordinal);
        Assert.DoesNotContain("query", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijk", result, StringComparison.Ordinal);
        Assert.DoesNotContain("known-secret-value", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScannerFindsReviewSubagentMemoryAndReferencedAgentConfig()
    {
        using var root = new TemporaryDirectory();
        string agent = Path.Combine(root.Path, "agent.config.toml");
        await File.WriteAllTextAsync(agent, "model = \"claude-agent\"\n");
        string projectRoot = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".codex"));
        string projectConfig = Path.Combine(projectRoot, ".codex", "config.toml");
        await File.WriteAllTextAsync(projectConfig, "review_model = \"gpt-project-review\"\n");
        string config = Path.Combine(root.Path, "config.toml");
        await File.WriteAllTextAsync(config, """
            model = "qwen-local"
            review_model = "gpt-review"

            [agents]
            default_subagent_model = "gpt-subagent"

            [agents.worker]
            config_file = "agent.config.toml"

            [memories]
            extract_model = "deepseek-cloud"
            consolidation_model = "gpt-memory"

            [profiles.offline]
            model = "gpt-profile"
            """ + $"\n\n[projects.'{projectRoot}']\ntrust_level = \"trusted\"\n");
        var scanner = new SecondaryModelOverrideScanner(new TomlConfigPatchEngine());
        IReadOnlyList<SecondaryModelOverride> results = await scanner.ScanAsync(config);
        Assert.Contains(results, item => item.KeyPath == "review_model");
        Assert.Contains(results, item => item.KeyPath == "agents.default_subagent_model");
        Assert.Contains(results, item => item.KeyPath == "memories.extract_model");
        Assert.Contains(results, item => item.KeyPath == "profiles.offline.model");
        Assert.Contains(results, item => item.FilePath == agent && item.KeyPath == "model");
        Assert.Contains(results, item => item.FilePath == projectConfig && item.KeyPath == "review_model");
        Assert.All(results.Where(item => item.Model.StartsWith("gpt", StringComparison.Ordinal)), item => Assert.True(item.IsPotentialCloudRequest));
    }

    [Fact]
    public void SecondaryPatcherChangesOnlyRequestedKeyAndPreservesComment()
    {
        string text = "review_model = \"gpt-old\" # keep\nmodel = \"main\"\n\n[mcp_servers.x]\ncommand = \"x\"\n";
        (string result, _) = SecondaryOverridePatcher.Apply(text, new Dictionary<string, string> { ["review_model"] = "qwen-local" });
        Assert.Contains("review_model = \"qwen-local\" # keep", result, StringComparison.Ordinal);
        Assert.Contains("model = \"main\"", result, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.x]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScannerAndPatcherSupportLiteralAndDottedModelKeys()
    {
        using var root = new TemporaryDirectory();
        string config = Path.Combine(root.Path, "config.toml");
        const string text = "agents.default_subagent_model = 'gpt-cloud' # keep literal comment\nmodel = \"main\"\n";
        await File.WriteAllTextAsync(config, text);
        var scanner = new SecondaryModelOverrideScanner(new TomlConfigPatchEngine());

        SecondaryModelOverride found = Assert.Single(await scanner.ScanAsync(config));
        Assert.Equal("agents.default_subagent_model", found.KeyPath);
        Assert.Equal("gpt-cloud", found.Model);
        Assert.Equal("'gpt-cloud'", found.RawTomlValue);
        Assert.True(found.IsPotentialCloudRequest);

        (string patched, IReadOnlyList<ConfigMutation> mutations) = SecondaryOverridePatcher.Apply(
            text,
            new Dictionary<string, string> { ["agents.default_subagent_model"] = "qwen-local" });
        Assert.Single(mutations);
        Assert.Contains("agents.default_subagent_model = \"qwen-local\" # keep literal comment", patched, StringComparison.Ordinal);
        Assert.Contains("model = \"main\"", patched, StringComparison.Ordinal);

        (string restored, _) = SecondaryOverridePatcher.Apply(
            patched,
            new Dictionary<string, SecondaryOverrideReplacement>
            {
                ["agents.default_subagent_model"] = new(found.Model, found.RawTomlValue),
            });
        Assert.Equal(text, restored);
    }

    [Fact]
    public async Task QuotedTableSegmentsWithDotsDoNotCollideWithNestedTables()
    {
        using var root = new TemporaryDirectory();
        string configPath = Path.Combine(root.Path, "config.toml");
        string config = """
            model = "gpt-5.6-sol"

            [agents."review.blue"]
            model = 'gpt-quoted'

            [agents.review.blue]
            model = "gpt-nested"
            """;
        await File.WriteAllTextAsync(configPath, config);
        var scanner = new SecondaryModelOverrideScanner(new TomlConfigPatchEngine());

        IReadOnlyList<SecondaryModelOverride> overrides = await scanner.ScanAsync(configPath);

        SecondaryModelOverride quoted = Assert.Single(overrides, item => item.Model == "gpt-quoted");
        Assert.Equal("agents.\"review.blue\".model", quoted.KeyPath);
        SecondaryModelOverride nested = Assert.Single(overrides, item => item.Model == "gpt-nested");
        Assert.Equal("agents.review.blue.model", nested.KeyPath);

        (string changed, IReadOnlyList<ConfigMutation> mutations) = SecondaryOverridePatcher.Apply(
            config,
            new Dictionary<string, string> { [quoted.KeyPath] = "local-only" });

        Assert.Single(mutations);
        Assert.Contains("model = \"local-only\"", changed, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-nested\"", changed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestAndLogNeverContainFullCredential()
    {
        const string secret = "sk-super-secret-fixture";
        using var root = new TemporaryDirectory();
        var home = new TestCodexHomeProvider(Path.Combine(root.Path, "home"));
        await File.WriteAllTextAsync(Path.Combine(home.Home, "config.toml"), $"model = \"deepseek\"\n\n[model_providers.deepseek]\nexperimental_bearer_token = \"{secret}\"\n");
        var backup = new BackupService(home, new AtomicBatchWriter(), new TomlConfigPatchEngine());
        string snapshot = await backup.EnsureInitialSnapshotAsync();
        string manifest = await File.ReadAllTextAsync(Path.Combine(snapshot, "manifest.json"));
        Assert.DoesNotContain(secret, manifest, StringComparison.Ordinal);

        var paths = new AppPaths(Path.Combine(root.Path, "local"));
        var redactor = new SecretRedactor();
        redactor.Register(secret);
        using (var logger = new AppLogger(paths, redactor))
        {
            logger.LogError("fixture " + secret + " Authorization: Bearer " + secret);
        }

        string log = await File.ReadAllTextAsync(Directory.EnumerateFiles(paths.LogsDirectory).Single());
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderStateNeverCopiesDormantBearerTableIntoAppSettings()
    {
        const string secret = "sk-dormant-provider-fixture";
        string config = $"model = \"gpt-native\"\n\n[model_providers.deepseek]\nname = \"deepseek\"\nexperimental_bearer_token = \"{secret}\"\n";
        using var harness = new SwitchHarness(config);

        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));
        await harness.Service.CommitAsync(plan);

        string settings = await File.ReadAllTextAsync(harness.AppPaths.SettingsPath);
        Assert.DoesNotContain(secret, settings, StringComparison.Ordinal);
        Assert.DoesNotContain("experimental_bearer_token", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SensitiveOpenAiBaseUrlQueryBlocksSwitchBeforeAppSettingsWrite()
    {
        const string secret = "query-secret-fixture";
        string config = $"model = \"gpt-native\"\nopenai_base_url = \"https://example.invalid/v1?api_key={secret}\"\n";
        using var harness = new SwitchHarness(config);
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CommitAsync(plan));

        Assert.Contains("疑似凭据", error.Message, StringComparison.Ordinal);
        Assert.Equal(config, harness.ReadConfig().Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.False(File.Exists(harness.AppPaths.SettingsPath));
    }
}
