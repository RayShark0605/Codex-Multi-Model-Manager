using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class SwitchMatrixTests
{
    [Theory]
    [InlineData(120_064, 95_488, 2_401)]
    [InlineData(65_536, 40_960, 2_048)]
    [InlineData(32_768, 16_384, 2_048)]
    [InlineData(262_144, 209_715, 4_096)]
    public void LocalContextPoliciesReserveBalancedHeadroom(int contextWindow, int expectedCompact, int expectedToolOutput)
    {
        Assert.Equal(expectedCompact, ConfigurationSwitchService.SuggestAutoCompact(contextWindow));
        Assert.Equal(expectedToolOutput, ConfigurationSwitchService.SuggestToolOutputLimit(contextWindow));
    }
    public static TheoryData<ProviderKind, ProviderKind> Transitions => new()
    {
        { ProviderKind.OpenAI, ProviderKind.DeepSeek },
        { ProviderKind.DeepSeek, ProviderKind.OpenAI },
        { ProviderKind.OpenAI, ProviderKind.LmStudio },
        { ProviderKind.LmStudio, ProviderKind.OpenAI },
        { ProviderKind.DeepSeek, ProviderKind.LmStudio },
        { ProviderKind.LmStudio, ProviderKind.DeepSeek },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task AllProviderTransitionsProduceValidPreservingToml(ProviderKind source, ProviderKind target)
    {
        using var harness = new SwitchHarness(SourceConfig(source));
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(target));
        byte[] candidate = Assert.Single(plan.Files).CandidateBytes!;
        string text = new UTF8Encoding(false, true).GetString(candidate);
        harness.Patch.Validate(text);
        Assert.Contains("[mcp_servers.demo]", text, StringComparison.Ordinal);
        Assert.Contains("[projects.'C:\\work']", text, StringComparison.Ordinal);
        Assert.Contains("[permissions.safe]", text, StringComparison.Ordinal);
        Assert.Contains("# user comment must survive", text, StringComparison.Ordinal);
        Assert.Equal(1, plan.Preservation.McpServerCount);
        Assert.Equal(1, plan.Preservation.ProjectCount);
        Assert.Contains($"model_provider = \"{ExpectedProviderId(target)}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnownedLmStudioLocalProviderTableIsPreserved()
    {
        const string custom = "model = \"qwen\"\nmodel_provider = \"lmstudio_local\"\n\n[model_providers.lmstudio_local]\nname = \"user owned\"\nbase_url = \"http://127.0.0.1:9999/v1\"\n";
        using var harness = new SwitchHarness(custom);
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI));
        string candidate = Encoding.UTF8.GetString(plan.Files.Single().CandidateBytes!);
        Assert.Contains("[model_providers.lmstudio_local]", candidate, StringComparison.Ordinal);
        Assert.Contains("base_url = \"http://127.0.0.1:9999/v1\"", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingOfficialDeepSeekBearerTableKeepsExactTextWhenChangingModel()
    {
        string original = SourceConfig(ProviderKind.DeepSeek).Replace("name = \"deepseek\"", "# official comment\nname  =  \"deepseek\"", StringComparison.Ordinal);
        using var harness = new SwitchHarness(original);
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.DeepSeek));
        string candidate = Encoding.UTF8.GetString(plan.Files.Single().CandidateBytes!);
        Assert.Contains("# official comment\nname  =  \"deepseek\"", candidate.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("experimental_bearer_token = \"sk-fixture-secret\"", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingOfficialDeepSeekEnvironmentAwayPreservesScriptOwnedBearerTable()
    {
        string providerTable = "[model_providers.deepseek]\n# official spacing\nname  =  \"deepseek\"\nbase_url = \"https://api.deepseek.com/\"\nwire_api = \"responses\"\nexperimental_bearer_token = \"sk-fixture-secret\"\n";
        string original = "model = \"deepseek-v4-pro\"\nmodel_provider = \"deepseek\"\nforced_login_method = \"api\"\n\n" + providerTable;
        using var harness = new SwitchHarness(original);

        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI));
        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(providerTable, candidate, StringComparison.Ordinal);
        Assert.Contains("model_provider = \"openai\"", candidate, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, warning => warning.Contains("官方脚本拥有", StringComparison.Ordinal));

        SwitchPlan localPlan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));
        string localCandidate = Encoding.UTF8.GetString(Assert.Single(localPlan.Files).CandidateBytes!).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(providerTable, localCandidate, StringComparison.Ordinal);
        Assert.Contains("model_provider = \"lmstudio\"", localCandidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalContextUsesLoadedContextAndSafeCompaction()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));
        string text = new UTF8Encoding(false, true).GetString(plan.Files.Single().CandidateBytes!);
        Assert.Contains("model_context_window = 65536", text, StringComparison.Ordinal);
        Assert.Contains($"model_auto_compact_token_limit = {ConfigurationSwitchService.SuggestAutoCompact(65_536)}", text, StringComparison.Ordinal);
        Assert.Contains("model_auto_compact_token_limit_scope = \"total\"", text, StringComparison.Ordinal);
        Assert.Contains($"tool_output_token_limit = {ConfigurationSwitchService.SuggestToolOutputLimit(65_536)}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("262144", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qwen120064CandidateUsesBalancedExactValues()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest request = harness.Request(ProviderKind.LmStudio) with
        {
            ContextWindow = 120_064,
            AutoCompactTokenLimit = 95_488,
            ToolOutputTokenLimit = 2_401,
            AutoCompactMode = AutoCompactMode.Automatic,
        };

        SwitchPlan plan = await harness.Service.CreatePlanAsync(request);
        string text = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);

        Assert.Contains("model_context_window = 120064", text, StringComparison.Ordinal);
        Assert.Contains("model_auto_compact_token_limit = 95488", text, StringComparison.Ordinal);
        Assert.Contains("model_auto_compact_token_limit_scope = \"total\"", text, StringComparison.Ordinal);
        Assert.Contains("tool_output_token_limit = 2401", text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task LocalContextPreferenceAndEndpointArePersistedOutsideCodexConfig()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest request = harness.Request(ProviderKind.LmStudio);
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(request));
        AppSettings saved = await harness.Settings.LoadAsync();
        ModelPreference preference = Assert.IsType<ModelPreference>(saved.ModelPreferences[request.TargetModel]);
        Assert.Equal(65_536, preference.LastLoadedContext);
        Assert.Equal(65_536, preference.CodexContext);
        Assert.Equal(ConfigurationSwitchService.SuggestAutoCompact(65_536), preference.AutoCompactTokenLimit);
        Assert.Equal(AutoCompactMode.Automatic, preference.AutoCompactMode);
        Assert.Equal(ConfigurationSwitchService.AutoCompactPolicyVersion, preference.AutoCompactPolicyVersion);
        Assert.Equal(ConfigurationSwitchService.SuggestToolOutputLimit(65_536), preference.ToolOutputTokenLimit);
        Assert.Equal(AppSettingsRepository.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.Equal("http://127.0.0.1:1234", saved.LmStudioEndpoint);
    }

    [Fact]
    public async Task InvalidCompactionIsRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest invalid = harness.Request(ProviderKind.LmStudio) with { AutoCompactTokenLimit = 65_536 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(invalid));
    }

    [Fact]
    public async Task CompactionWithLessThan1024TokensHeadroomIsRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest invalid = harness.Request(ProviderKind.LmStudio) with { AutoCompactTokenLimit = 64_513 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(invalid));
    }

    [Fact]
    public async Task InvalidToolOutputLimitIsRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest invalid = harness.Request(ProviderKind.LmStudio) with
        {
            ToolOutputTokenLimit = ConfigurationSwitchService.SuggestAutoCompact(65_536),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(invalid));
    }

    [Fact]
    public async Task ManualCompactAboveBalancedSuggestionWarnsWithoutChangingValue()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest manual = harness.Request(ProviderKind.LmStudio) with
        {
            AutoCompactTokenLimit = 50_000,
            AutoCompactMode = AutoCompactMode.Manual,
        };

        SwitchPlan plan = await harness.Service.CreatePlanAsync(manual);
        string text = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);

        Assert.Contains("model_auto_compact_token_limit = 50000", text, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, warning => warning.Contains("高于平衡策略建议值", StringComparison.Ordinal));
    }
    [Fact]
    public async Task UnsupportedLocalReasoningEffortIsRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest invalid = harness.Request(ProviderKind.LmStudio) with { ReasoningEffort = "on" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(invalid));
    }

    [Fact]
    public async Task StaleMediumReasoningIsRejectedWhenLmStudioOnlyReportsOnOff()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest invalid = harness.Request(ProviderKind.LmStudio) with
        {
            ReasoningEffort = "medium",
            TargetAllowedCodexReasoningEfforts = ReasoningEffortPolicy.CanonicalizeAllowed(["on", "off"]),
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(invalid));

        Assert.Contains("必须选择不写入", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactProviderReasoningIntersectionCanBeWritten()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest request = harness.Request(ProviderKind.LmStudio) with
        {
            ReasoningEffort = "medium",
            TargetAllowedCodexReasoningEfforts = ReasoningEffortPolicy.CanonicalizeAllowed(["on", "medium", "off"]),
        };

        SwitchPlan plan = await harness.Service.CreatePlanAsync(request);
        string candidate = Encoding.UTF8.GetString(Assert.Single(plan.Files).CandidateBytes!);

        Assert.Contains("model_reasoning_effort = \"medium\"", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public void OnOffReasoningOptionsDoNotMapToCodexEffort()
    {
        Assert.Equal(string.Empty, ReasoningEffortPolicy.CanonicalizeAllowed(["on", "off"]));
        Assert.Empty(ReasoningEffortPolicy.ParseAllowed(string.Empty));
    }

    [Fact]
    public async Task UnknownProviderIsRejected()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        var request = new SwitchRequest(ProviderKind.Unknown, "anything");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(request));
    }

    [Fact]
    public async Task OldCodexVersionBlocksDeepSeekMetadata()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig, "codex-cli 0.143.9");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.DeepSeek)));
        Assert.Contains("至少 0.144.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitRoundTripRestoresOpenAiProviderSpecificState()
    {
        string initial = "forced_login_method = \"chatgpt\"\nmodel = \"gpt-custom\"\nmodel_reasoning_effort = \"max\"\nmodel_auto_compact_token_limit_scope = \"body_after_prefix\"\n\n[mcp_servers.demo]\ncommand = \"demo\"\n";
        using var harness = new SwitchHarness(initial);
        SwitchPlan toDeepSeek = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.DeepSeek));
        await harness.Service.CommitAsync(toDeepSeek);
        Assert.Contains("forced_login_method = \"api\"", harness.ReadConfig(), StringComparison.Ordinal);
        Assert.DoesNotContain("model_auto_compact_token_limit_scope", harness.ReadConfig(), StringComparison.Ordinal);
        SwitchPlan toOpenAi = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI));
        await harness.Service.CommitAsync(toOpenAi);
        string restored = harness.ReadConfig();
        Assert.Contains("forced_login_method = \"chatgpt\"", restored, StringComparison.Ordinal);
        Assert.Contains("model_auto_compact_token_limit_scope = \"body_after_prefix\"", restored, StringComparison.Ordinal);
        Assert.DoesNotContain("[model_providers.deepseek]", restored, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.demo]", restored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalToolOutputLimitIsRemovedWhenOpenAiOriginallyHadNoValue()
    {
        const string initial = "model = \"gpt-custom\"\n";
        using var harness = new SwitchHarness(initial);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio)));
        Assert.Contains("tool_output_token_limit = 2048", harness.ReadConfig(), StringComparison.Ordinal);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI)));
        Assert.DoesNotContain("tool_output_token_limit", harness.ReadConfig(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task LocalToolOutputLimitRestoresExactOpenAiValue()
    {
        const string initial = "model = \"gpt-custom\"\ntool_output_token_limit = 7777\n";
        using var harness = new SwitchHarness(initial);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio)));
        Assert.Contains("tool_output_token_limit = 2048", harness.ReadConfig(), StringComparison.Ordinal);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI)));
        Assert.Contains("tool_output_token_limit = 7777", harness.ReadConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalToolOutputLimitRestoresExactDeepSeekValue()
    {
        string initial = SourceConfig(ProviderKind.DeepSeek).Replace("model_reasoning_effort = \"max\"", "model_reasoning_effort = \"high\"\ntool_output_token_limit = 3333", StringComparison.Ordinal);
        using var harness = new SwitchHarness(initial);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio)));
        Assert.Contains("tool_output_token_limit = 2048", harness.ReadConfig(), StringComparison.Ordinal);

        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.DeepSeek)));
        Assert.Contains("tool_output_token_limit = 3333", harness.ReadConfig(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task FollowMainSecondaryOverrideAndRestoreOriginal()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest local = harness.Request(ProviderKind.LmStudio) with { SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(local));
        Assert.Contains("review_model = \"qwen/local@q6\"", harness.ReadConfig(), StringComparison.Ordinal);
        SwitchRequest openAi = harness.Request(ProviderKind.OpenAI) with { SecondaryOverridePolicy = SecondaryOverridePolicy.RestoreOriginal };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(openAi));
        Assert.Contains("review_model = \"gpt-review\"", harness.ReadConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningCodexBlocksCommitBeforeWrite()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        var runningRuntime = new FakeRuntimeProbe(harness.Home, true);
        var service = new ConfigurationSwitchService(harness.Home, harness.Patch, harness.Writer, harness.Backups, harness.Scanner, runningRuntime, harness.Settings, harness.Secrets, harness.Preflight);
        SwitchPlan plan = await service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(plan));
        Assert.Equal(SwitchHarness.BaseConfig.Replace("\r\n", "\n", StringComparison.Ordinal), harness.ReadConfig().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LmStudioPreflightFailureBlocksPreviewWithoutBackupOrWrite()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        harness.Preflight.DefaultResult = FakeLmStudioSwitchPreflight.Fail();

        LmStudioCompatibilityException error = await Assert.ThrowsAsync<LmStudioCompatibilityException>(
            () => harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio)));

        Assert.Equal(CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder, error.Result.FailureCode);
        Assert.Equal(1, harness.Preflight.CallCount);
        Assert.False(Directory.Exists(harness.Backups.BackupRoot));
        Assert.Equal(SwitchHarness.BaseConfig.Replace("\r\n", "\n", StringComparison.Ordinal), harness.ReadConfig().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LmStudioPreflightIsRepeatedBeforeCommitAndFailureCreatesNoBackup()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan preview = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));
        harness.Preflight.Enqueue(FakeLmStudioSwitchPreflight.Fail());

        await Assert.ThrowsAsync<LmStudioCompatibilityException>(() => harness.Service.CommitAsync(preview));

        Assert.Equal(2, harness.Preflight.CallCount);
        Assert.False(Directory.Exists(harness.Backups.BackupRoot));
        Assert.Equal(SwitchHarness.BaseConfig.Replace("\r\n", "\n", StringComparison.Ordinal), harness.ReadConfig().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PassingLmStudioPreflightRunsForPreviewAndCommit()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchPlan preview = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.LmStudio));

        await harness.Service.CommitAsync(preview);

        Assert.Equal(2, harness.Preflight.CallCount);
        Assert.True(Directory.Exists(harness.Backups.BackupRoot));
        Assert.Contains("model_provider = \"lmstudio\"", harness.ReadConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConfigIsCreatedTransactionallyAndInitialRecordsMissingState()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        string config = Path.Combine(harness.Home.Home, "config.toml");
        File.Delete(config);

        SwitchPlan plan = await harness.Service.CreatePlanAsync(harness.Request(ProviderKind.OpenAI));
        await harness.Service.CommitAsync(plan);

        Assert.True(File.Exists(config));
        harness.Patch.Validate(harness.ReadConfig());
        string manifest = await File.ReadAllTextAsync(Path.Combine(harness.Backups.BackupRoot, "initial", "manifest.json"));
        Assert.Contains("\"relativeName\": \"config.toml\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"existed\": false", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedNonDefaultLmStudioUsesOnlyNamespacedCustomProvider()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest request = harness.Request(ProviderKind.LmStudio) with
        {
            LmStudioProviderId = "lmstudio_local_cmm",
            LmStudioEndpoint = new Uri("http://127.0.0.1:5678"),
            LmStudioRequiresAuthentication = true,
        };

        SwitchPlan plan = await harness.Service.CreatePlanAsync(request);
        string candidate = Encoding.UTF8.GetString(plan.Files.Single().CandidateBytes!);
        Assert.Contains("model_provider = \"lmstudio_local_cmm\"", candidate, StringComparison.Ordinal);
        Assert.Contains("[model_providers.lmstudio_local_cmm]", candidate, StringComparison.Ordinal);
        Assert.Contains("[model_providers.lmstudio_local_cmm.auth]", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("[model_providers.lmstudio]", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("lm-test", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownEmbeddingInstanceCannotBeConfiguredAsCodexModel()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        SwitchRequest request = harness.Request(ProviderKind.LmStudio) with { TargetModel = "embedding", TargetModelType = "embedding" };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.CreatePlanAsync(request));
        Assert.Contains("非 LLM", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitExternalOverrideGetsSupplementalBaselineAndRestoresOriginal()
    {
        string placeholder = "model = \"gpt-native\"\n";
        using var harness = new SwitchHarness(placeholder);
        string external = Path.Combine(Path.GetDirectoryName(harness.HelperPath)!, "worker.config.toml");
        await File.WriteAllTextAsync(external, "# keep external\nmodel = 'gpt-agent'\n");
        string config = $"model = \"gpt-native\"\n\n[agents.worker]\nconfig_file = {JsonSerializer.Serialize(external)}\n";
        await File.WriteAllTextAsync(Path.Combine(harness.Home.Home, "config.toml"), config);
        string selection = JsonSerializer.Serialize(new[] { new SecondaryOverrideTarget(external, "model") });
        SwitchRequest follow = harness.Request(ProviderKind.LmStudio) with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain,
            SecondaryOverrideSelectionJson = selection,
        };

        SwitchPlan followPlan = await harness.Service.CreatePlanAsync(follow);
        Assert.Equal(2, followPlan.Files.Count);
        await harness.Service.CommitAsync(followPlan);
        Assert.Contains("model = \"qwen/local@q6\"", await File.ReadAllTextAsync(external), StringComparison.Ordinal);
        Assert.Contains("# keep external", await File.ReadAllTextAsync(external), StringComparison.Ordinal);
        string supplementalRoot = Path.Combine(harness.Backups.BackupRoot, "supplemental-baseline");
        string baseline = Assert.Single(Directory.EnumerateDirectories(supplementalRoot));
        Assert.Equal("# keep external\nmodel = 'gpt-agent'\n", (await File.ReadAllTextAsync(Path.Combine(baseline, "content.toml"))).Replace("\r\n", "\n", StringComparison.Ordinal));
        BackupSnapshotInfo firstHistory = Assert.Single(await harness.Backups.ListHistoryAsync());
        Assert.Contains(firstHistory.Manifest.Files, file => file.RelativeName.StartsWith("supplemental", StringComparison.OrdinalIgnoreCase) && Path.GetFullPath(file.OriginalPath) == Path.GetFullPath(external));

        SwitchRequest restore = harness.Request(ProviderKind.OpenAI) with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.RestoreOriginal,
            SecondaryOverrideSelectionJson = selection,
        };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(restore));
        Assert.Equal("# keep external\nmodel = 'gpt-agent'\n", (await File.ReadAllTextAsync(external)).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Empty((await harness.Settings.LoadAsync()).SecondaryOverrideOriginals);
    }

    [Fact]
    public async Task HistoryRestoreIncludesExplicitExternalOverrideFile()
    {
        using var harness = new SwitchHarness("model = \"gpt-native\"\n");
        string external = Path.Combine(Path.GetDirectoryName(harness.HelperPath)!, "history-worker.config.toml");
        await File.WriteAllTextAsync(external, "model = \"gpt-history\"\n");
        await File.WriteAllTextAsync(Path.Combine(harness.Home.Home, "config.toml"), $"model = \"gpt-native\"\n\n[agents.worker]\nconfig_file = {JsonSerializer.Serialize(external)}\n");
        string selection = JsonSerializer.Serialize(new[] { new SecondaryOverrideTarget(external, "model") });
        SwitchRequest follow = harness.Request(ProviderKind.LmStudio) with { SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain, SecondaryOverrideSelectionJson = selection };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(follow));
        BackupSnapshotInfo original = Assert.Single(await harness.Backups.ListHistoryAsync());

        await harness.Backups.RestoreAsync(original.Directory);

        Assert.Equal("model = \"gpt-history\"\n", (await File.ReadAllTextAsync(external)).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("model = \"gpt-native\"", harness.ReadConfig(), StringComparison.Ordinal);
        Assert.Equal(2, (await harness.Backups.ListHistoryAsync()).Count);
    }

    [Fact]
    public async Task InitialRestoreIncludesEverySupplementalBaseline()
    {
        using var harness = new SwitchHarness("model = \"gpt-native\"\n");
        string external = Path.Combine(Path.GetDirectoryName(harness.HelperPath)!, "initial-worker.config.toml");
        await File.WriteAllTextAsync(external, "# original\nmodel = \"gpt-initial\"\n");
        await File.WriteAllTextAsync(Path.Combine(harness.Home.Home, "config.toml"), $"model = \"gpt-native\"\n\n[agents.worker]\nconfig_file = {JsonSerializer.Serialize(external)}\n");
        string selection = JsonSerializer.Serialize(new[] { new SecondaryOverrideTarget(external, "model") });
        SwitchRequest follow = harness.Request(ProviderKind.LmStudio) with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain,
            SecondaryOverrideSelectionJson = selection,
        };
        await harness.Service.CommitAsync(await harness.Service.CreatePlanAsync(follow));
        Assert.Contains("qwen/local@q6", await File.ReadAllTextAsync(external), StringComparison.Ordinal);

        await harness.Backups.RestoreAsync(Path.Combine(harness.Backups.BackupRoot, "initial"));

        Assert.Equal("# original\nmodel = \"gpt-initial\"\n", (await File.ReadAllTextAsync(external)).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("model = \"gpt-native\"", harness.ReadConfig(), StringComparison.Ordinal);
        BackupSnapshotInfo restoreBackup = (await harness.Backups.ListHistoryAsync())[0];
        Assert.Equal(BackupOperation.RestoreInitial, restoreBackup.Manifest.Operation);
        Assert.Contains(restoreBackup.Manifest.Files, file => Path.GetFullPath(file.OriginalPath) == Path.GetFullPath(external));
    }

    [Fact]
    public async Task ExternalOverrideChangeAfterPreviewStopsBeforeCreatingBackups()
    {
        using var harness = new SwitchHarness("model = \"gpt-native\"\n");
        string external = Path.Combine(Path.GetDirectoryName(harness.HelperPath)!, "racing-worker.config.toml");
        await File.WriteAllTextAsync(external, "model = \"gpt-before\"\n");
        await File.WriteAllTextAsync(Path.Combine(harness.Home.Home, "config.toml"), $"model = \"gpt-native\"\n\n[agents.worker]\nconfig_file = {JsonSerializer.Serialize(external)}\n");
        string selection = JsonSerializer.Serialize(new[] { new SecondaryOverrideTarget(external, "model") });
        SwitchRequest request = harness.Request(ProviderKind.LmStudio) with
        {
            SecondaryOverridePolicy = SecondaryOverridePolicy.FollowMain,
            SecondaryOverrideSelectionJson = selection,
        };
        SwitchPlan preview = await harness.Service.CreatePlanAsync(request);
        await File.WriteAllTextAsync(external, "model = \"gpt-after\"\n");

        IOException error = await Assert.ThrowsAsync<IOException>(() => harness.Service.CommitAsync(preview));

        Assert.Contains("预览后发生变化", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(harness.Backups.BackupRoot));
        Assert.Equal("model = \"gpt-after\"\n", (await File.ReadAllTextAsync(external)).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string SourceConfig(ProviderKind provider)
    {
        string root = SwitchHarness.BaseConfig;
        return provider switch
        {
            ProviderKind.OpenAI => root,
            ProviderKind.DeepSeek => root.Replace("model = \"gpt-old\"", "model = \"deepseek-v4-pro\"\nmodel_provider = \"deepseek\"", StringComparison.Ordinal) + "\n[model_providers.deepseek]\nname = \"deepseek\"\nbase_url = \"https://api.deepseek.com/\"\nwire_api = \"responses\"\nexperimental_bearer_token = \"sk-fixture-secret\"\n",
            ProviderKind.LmStudio => root.Replace("model = \"gpt-old\"", "model = \"qwen/local@q6\"\nmodel_provider = \"lmstudio\"\nmodel_context_window = 65536\nmodel_auto_compact_token_limit = 57344", StringComparison.Ordinal),
            _ => root,
        };
    }

    private static string ExpectedProviderId(ProviderKind provider) => provider switch
    {
        ProviderKind.OpenAI => "openai",
        ProviderKind.DeepSeek => "deepseek",
        ProviderKind.LmStudio => "lmstudio",
        _ => "unknown",
    };
}
