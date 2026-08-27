using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class ReviewCoreRemediationTests
{
    [Fact]
    public void QuotedRootKeysAreUpdatedInPlaceWithoutMatchingQuotedDots()
    {
        const string original = """
            "model"   = "old" # keep model comment
            'model_reasoning_effort' = 'low' # keep effort comment
            "a.b" = "unmanaged"
            future = 7
            """;
        var engine = new TomlConfigPatchEngine();

        ConfigPatchResult result = engine.Apply(
            original,
            new ConfigPatchRequest(
                new Dictionary<string, string?>
                {
                    ["model"] = "\"new\"",
                    ["model_reasoning_effort"] = "\"high\"",
                },
                new Dictionary<string, string?>()));

        Assert.Contains("\"model\"   = \"new\" # keep model comment", result.Text, StringComparison.Ordinal);
        Assert.Contains("'model_reasoning_effort' = \"high\" # keep effort comment", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"a.b\" = \"unmanaged\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("future = 7", result.Text, StringComparison.Ordinal);
        Assert.Equal(2, result.Mutations.Count);
        engine.Validate(result.Text);
    }

    [Fact]
    public void BareCarriageReturnTomlIsRejectedBeforePatching()
    {
        const string original = "model = \"old\"\rreview_model = \"preserve\"\r";
        var engine = new TomlConfigPatchEngine();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => engine.Apply(
            original,
            new ConfigPatchRequest(
                new Dictionary<string, string?> { ["model"] = "\"new\"" },
                new Dictionary<string, string?>())));

        Assert.Contains("Invalid \\r", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LmStudioNullAndExplicitCompactContractsNormalizeDeterministically()
    {
        const int context = 65_536;
        int suggestedCompact = ConfigurationSwitchService.SuggestAutoCompact(context);
        int suggestedToolOutput = ConfigurationSwitchService.SuggestToolOutputLimit(context);
        SwitchRequest automaticByOmission = ConfigurationSwitchService.NormalizeSwitchRequest(CreateLmRequest(context));
        SwitchRequest automaticByMatchingValue = ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with { AutoCompactTokenLimit = suggestedCompact });
        SwitchRequest manualByValue = ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with { AutoCompactTokenLimit = suggestedCompact - 1_024 });
        SwitchRequest explicitAutomatic = ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with { AutoCompactMode = AutoCompactMode.Automatic });
        SwitchRequest explicitManual = ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with
            {
                AutoCompactMode = AutoCompactMode.Manual,
                AutoCompactTokenLimit = suggestedCompact - 2_048,
            });

        Assert.Equal((suggestedCompact, AutoCompactMode.Automatic, suggestedToolOutput),
            (automaticByOmission.AutoCompactTokenLimit, automaticByOmission.AutoCompactMode, automaticByOmission.ToolOutputTokenLimit));
        Assert.Equal(AutoCompactMode.Automatic, automaticByMatchingValue.AutoCompactMode);
        Assert.Equal(AutoCompactMode.Manual, manualByValue.AutoCompactMode);
        Assert.Equal(suggestedCompact, explicitAutomatic.AutoCompactTokenLimit);
        Assert.Equal(AutoCompactMode.Manual, explicitManual.AutoCompactMode);
        Assert.Equal("high", ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with { ReasoningEffort = "  High " }).ReasoningEffort);

        Assert.Throws<InvalidOperationException>(() => ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with
            {
                AutoCompactMode = AutoCompactMode.Automatic,
                AutoCompactTokenLimit = suggestedCompact - 1,
            }));
        Assert.Throws<InvalidOperationException>(() => ConfigurationSwitchService.NormalizeSwitchRequest(
            CreateLmRequest(context) with { AutoCompactMode = AutoCompactMode.Manual }));
    }

    [Fact]
    public async Task SwitchPlanAndPersistedPreferenceUseTheSameNormalizedValues()
    {
        using var harness = new SwitchHarness(SwitchHarness.BaseConfig);
        const int context = 65_536;
        SwitchRequest request = CreateLmRequest(context) with
        {
            LmStudioEndpoint = new Uri("http://127.0.0.1:1234"),
            CredentialHelperPath = harness.HelperPath,
        };

        SwitchPlan plan = await harness.Service.CreatePlanAsync(request);
        await harness.Service.CommitAsync(plan);
        AppSettings settings = await harness.Settings.LoadAsync();
        ModelPreference preference = Assert.IsType<ModelPreference>(settings.ModelPreferences[request.TargetModel]);

        Assert.Equal(AutoCompactMode.Automatic, plan.Request.AutoCompactMode);
        Assert.Equal(ConfigurationSwitchService.SuggestAutoCompact(context), plan.Request.AutoCompactTokenLimit);
        Assert.Equal(ConfigurationSwitchService.SuggestToolOutputLimit(context), plan.Request.ToolOutputTokenLimit);
        Assert.Equal(plan.Request.AutoCompactMode, preference.AutoCompactMode);
        Assert.Equal(plan.Request.AutoCompactTokenLimit, preference.AutoCompactTokenLimit);
        Assert.Equal(plan.Request.ToolOutputTokenLimit, preference.ToolOutputTokenLimit);
    }

    [Theory]
    [InlineData("https://example.test/v1?key=x", true)]
    [InlineData("https://example.test/v1?api_key=x", true)]
    [InlineData("https://example.test/v1?api%5Fkey=x", true)]
    [InlineData("https://example.test/v1?client-secret=x", true)]
    [InlineData("https://example.test/v1?accessToken=x", true)]
    [InlineData("https://example.test/v1?monkey=x", false)]
    [InlineData("https://example.test/v1?keyboard=x", false)]
    [InlineData("https://example.test/v1?donkey_tokenizer=x", false)]
    [InlineData("https://example.test/v1", false)]
    public void SensitiveQueryDetectionUsesTokenBoundaries(string value, bool expected)
    {
        Assert.Equal(expected, ConfigurationSwitchService.ContainsSensitiveUrlQuery('"' + value + '"'));
    }

    [Fact]
    public async Task CorruptSettingsAreQuarantinedByteForByteAndDefaulted()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.EnsureDirectories();
        byte[] corrupt = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"lmStudioEndpoint\":\"http://127.0.0.1:1234\",\"modelPreferences\":null,\"providerStates\":{},\"secondaryOverrideOriginals\":{}}");
        await File.WriteAllBytesAsync(paths.SettingsPath, corrupt);
        var repository = new AppSettingsRepository(paths);

        AppSettingsLoadResult result = await repository.LoadWithRecoveryAsync();

        Assert.True(result.RecoveredCorruptSettings);
        Assert.False(File.Exists(paths.SettingsPath));
        Assert.NotNull(result.RecoveredCorruptFilePath);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(result.RecoveredCorruptFilePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(corrupt)), result.RecoveredCorruptSha256);
        Assert.Equal(nameof(InvalidDataException), result.RecoveredCorruptExceptionType);
        Assert.Equal("http://127.0.0.1:1234", result.Settings.LmStudioEndpoint);
        Assert.Matches(@"appsettings\.corrupt-\d{8}-\d{9}-[0-9a-f]{32}\.json$", result.RecoveredCorruptFilePath);
    }

    [Fact]
    public async Task ConcurrentValidSettingsReplacementIsNeverDeletedDuringRecovery()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.EnsureDirectories();
        byte[] corrupt = Encoding.UTF8.GetBytes("{broken");
        byte[] replacement = AppSettingsRepository.Serialize(new AppSettings
        {
            LmStudioEndpoint = "http://127.0.0.1:7777",
        });
        await File.WriteAllBytesAsync(paths.SettingsPath, corrupt);
        int hookCalls = 0;
        var repository = new AppSettingsRepository(paths, async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref hookCalls) == 1)
            {
                await File.WriteAllBytesAsync(paths.SettingsPath, replacement, cancellationToken);
            }
        });

        AppSettingsLoadResult result = await repository.LoadWithRecoveryAsync();

        Assert.True(result.RecoveredCorruptSettings);
        Assert.Equal("http://127.0.0.1:7777", result.Settings.LmStudioEndpoint);
        Assert.True(File.Exists(paths.SettingsPath));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(paths.SettingsPath));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(result.RecoveredCorruptFilePath!));
    }

    [Fact]
    public async Task SameByteConcurrentRewriteIsDetectedByFullFingerprintBeforeDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.EnsureDirectories();
        byte[] corrupt = Encoding.UTF8.GetBytes("{broken");
        await File.WriteAllBytesAsync(paths.SettingsPath, corrupt);
        int hookCalls = 0;
        var repository = new AppSettingsRepository(paths, async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref hookCalls) == 1)
            {
                await Task.Delay(20, cancellationToken);
                await File.WriteAllBytesAsync(paths.SettingsPath, corrupt, cancellationToken);
            }
        });

        AppSettingsLoadResult result = await repository.LoadWithRecoveryAsync();

        Assert.True(result.RecoveredCorruptSettings);
        Assert.Equal(2, hookCalls);
        Assert.Equal(2, Directory.EnumerateFiles(paths.Root, "appsettings.corrupt-*.json").Count());
        Assert.False(File.Exists(paths.SettingsPath));
    }

    [Fact]
    public async Task OrdinarySettingsIoFailureIsNotReportedAsRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.EnsureDirectories();
        await File.WriteAllBytesAsync(paths.SettingsPath, AppSettingsRepository.Serialize(new AppSettings()));
        await using var locked = new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var repository = new AppSettingsRepository(paths);

        await Assert.ThrowsAnyAsync<IOException>(() => repository.LoadWithRecoveryAsync());
        Assert.Empty(Directory.EnumerateFiles(paths.Root, "appsettings.corrupt-*.json"));
    }

    [Theory]
    [InlineData("0.46", "0.46.0", true)]
    [InlineData("1.2.3", "1.2.3.0", true)]
    [InlineData("1.2.3.0", "1.2.3", true)]
    [InlineData("1.2.0-rc.1", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0-rc.99", true)]
    [InlineData("1.2.0-rc.10", "1.2.0-rc.2", true)]
    [InlineData("1.2.0-1", "1.2.0-alpha", false)]
    [InlineData("1.2.0-alpha.1", "1.2.0-alpha", true)]
    [InlineData("codex 1.2.3+build.9", "1.2.3+other", true)]
    public void SemanticVersionHonorsMissingZeroAndPrereleaseOrdering(
        string actual,
        string required,
        bool expected)
    {
        Assert.Equal(expected, SemanticVersion.IsAtLeast(actual, required));
    }

    [Fact]
    public void ReasoningEffortIsTrimmedAndCanonicalizedCaseInsensitively()
    {
        Assert.Equal("high,low,max", ReasoningEffortPolicy.CanonicalizeAllowed([" High ", "LOW", "Max", "unknown"]));
        Assert.Collection(
            ReasoningEffortPolicy.ParseAllowed(" High,LOW ").Order(StringComparer.Ordinal),
            value => Assert.Equal("high", value),
            value => Assert.Equal("low", value));
    }

    [Fact]
    public async Task MultilineTomlStringPseudoOverridesAreNeitherScannedNorPatched()
    {
        using var temporary = new TemporaryDirectory();
        string configPath = Path.Combine(temporary.Path, "config.toml");
        const string text = """"
            model = "main"
            payload = """
            review_model = "gpt-fake"
            [profiles.fake]
            model = "gpt-fake"
            """
            review_model = "gpt-real"
            """";
        await File.WriteAllTextAsync(configPath, text);
        var scanner = new SecondaryModelOverrideScanner(new TomlConfigPatchEngine());

        SecondaryModelOverride item = Assert.Single(await scanner.ScanAsync(configPath));
        (string patched, IReadOnlyList<ConfigMutation> mutations) = SecondaryOverridePatcher.Apply(
            text,
            new Dictionary<string, string>
            {
                ["review_model"] = "local-real",
                ["profiles.fake.model"] = "local-fake",
            });

        Assert.Equal("review_model", item.KeyPath);
        Assert.Equal("gpt-real", item.Model);
        Assert.Single(mutations);
        Assert.Contains("review_model = \"gpt-fake\"", patched, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-fake\"", patched, StringComparison.Ordinal);
        Assert.Contains("review_model = \"local-real\"", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("local-fake", patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidReferencedAgentConfigProducesScanErrorWithoutBlockingPrimary()
    {
        using var temporary = new TemporaryDirectory();
        string primary = Path.Combine(temporary.Path, "config.toml");
        string referenced = Path.Combine(temporary.Path, "bad.config.toml");
        await File.WriteAllTextAsync(primary, "model = \"main\"\n[agents.worker]\nconfig_file = \"bad.config.toml\"\n");
        await File.WriteAllTextAsync(referenced, "model = [\n");
        var scanner = new SecondaryModelOverrideScanner(new TomlConfigPatchEngine());

        SecondaryModelOverride error = Assert.Single(await scanner.ScanAsync(primary));

        Assert.Equal("<scan_error>", error.KeyPath);
        Assert.Equal(Path.GetFullPath(referenced), error.FilePath);
        Assert.False(error.CanEdit);
    }

    [Fact]
    public async Task MissingOrInaccessibleReferencedAgentConfigProducesScanError()
    {
        using var temporary = new TemporaryDirectory();
        string primary = Path.Combine(temporary.Path, "config.toml");
        string referenced = Path.Combine(temporary.Path, "missing.config.toml");
        await File.WriteAllTextAsync(primary, "model = \"main\"\n[agents.worker]\nconfig_file = \"missing.config.toml\"\n");
        var scanner = new SecondaryModelOverrideScanner(new TomlConfigPatchEngine());

        SecondaryModelOverride error = Assert.Single(await scanner.ScanAsync(primary));

        Assert.Equal("<scan_error>", error.KeyPath);
        Assert.Equal(Path.GetFullPath(referenced), error.FilePath);
        Assert.Contains("不存在", error.Detail, StringComparison.Ordinal);
    }

    private static SwitchRequest CreateLmRequest(int context) => new(
        ProviderKind.LmStudio,
        "fixture@loaded",
        ContextWindow: context,
        LmStudioProviderId: "lmstudio",
        LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));
}
