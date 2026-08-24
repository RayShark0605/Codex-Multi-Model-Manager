using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class AppSettingsMigrationTests
{
    [Fact]
    public void LegacyAutomaticCompactPreferenceMigratesToBalancedPolicy()
    {
        const int contextWindow = 120_064;
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            ModelPreferences = new Dictionary<string, ModelPreference>(StringComparer.Ordinal)
            {
                ["qwen3.8-27b@q6_k_xl"] = new()
                {
                    LastLoadedContext = contextWindow,
                    CodexContext = contextWindow,
                    AutoCompactTokenLimit = 108_057,
                },
            },
        };

        AppSettings migrated = AppSettingsRepository.Migrate(settings);
        ModelPreference preference = migrated.ModelPreferences["qwen3.8-27b@q6_k_xl"];

        Assert.Equal(AppSettingsRepository.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(AutoCompactMode.Automatic, preference.AutoCompactMode);
        Assert.Equal(ConfigurationSwitchService.AutoCompactPolicyVersion, preference.AutoCompactPolicyVersion);
        Assert.Equal(95_488, preference.AutoCompactTokenLimit);
        Assert.Equal(2_401, preference.ToolOutputTokenLimit);
    }

    [Fact]
    public void LegacyNonFormulaCompactPreferenceMigratesAsManualWithoutChangingValue()
    {
        const int contextWindow = 120_064;
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            ModelPreferences = new Dictionary<string, ModelPreference>(StringComparer.Ordinal)
            {
                ["qwen3.8-27b@q6_k_xl"] = new()
                {
                    LastLoadedContext = contextWindow,
                    CodexContext = contextWindow,
                    AutoCompactTokenLimit = 100_000,
                },
            },
        };

        AppSettings migrated = AppSettingsRepository.Migrate(settings);
        ModelPreference preference = migrated.ModelPreferences["qwen3.8-27b@q6_k_xl"];

        Assert.Equal(AutoCompactMode.Manual, preference.AutoCompactMode);
        Assert.Equal(100_000, preference.AutoCompactTokenLimit);
        Assert.Equal(2_401, preference.ToolOutputTokenLimit);
    }

    [Fact]
    public void LegacyMissingCompactPreferenceMigratesToAutomaticPolicy()
    {
        const int contextWindow = 120_064;
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            ModelPreferences = new Dictionary<string, ModelPreference>(StringComparer.Ordinal)
            {
                ["qwen3.8-27b@q6_k_xl"] = new()
                {
                    LastLoadedContext = contextWindow,
                    CodexContext = contextWindow,
                },
            },
        };

        AppSettings migrated = AppSettingsRepository.Migrate(settings);
        ModelPreference preference = migrated.ModelPreferences["qwen3.8-27b@q6_k_xl"];

        Assert.Equal(AutoCompactMode.Automatic, preference.AutoCompactMode);
        Assert.Equal(95_488, preference.AutoCompactTokenLimit);
        Assert.Equal(2_401, preference.ToolOutputTokenLimit);
    }

    [Fact]
    public void ChangedLoadedContextDoesNotReuseManualCompactValue()
    {
        var preference = new ModelPreference
        {
            LastLoadedContext = 120_064,
            AutoCompactTokenLimit = 100_000,
            AutoCompactMode = AutoCompactMode.Manual,
            AutoCompactPolicyVersion = ConfigurationSwitchService.AutoCompactPolicyVersion,
        };

        (int limit, AutoCompactMode mode) = ConfigurationSwitchService.ResolveAutoCompactPreference(preference, 65_536);

        Assert.Equal(40_960, limit);
        Assert.Equal(AutoCompactMode.Automatic, mode);
    }

    [Fact]
    public void SameContextKeepsValidManualCompactValue()
    {
        var preference = new ModelPreference
        {
            LastLoadedContext = 120_064,
            AutoCompactTokenLimit = 100_000,
            AutoCompactMode = AutoCompactMode.Manual,
            AutoCompactPolicyVersion = ConfigurationSwitchService.AutoCompactPolicyVersion,
        };

        (int limit, AutoCompactMode mode) = ConfigurationSwitchService.ResolveAutoCompactPreference(preference, 120_064);

        Assert.Equal(100_000, limit);
        Assert.Equal(AutoCompactMode.Manual, mode);
    }
}
