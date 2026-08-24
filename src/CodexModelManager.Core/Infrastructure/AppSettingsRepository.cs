using System.Text.Json;
using System.Text.Json.Serialization;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public sealed class AppSettingsRepository(AppPaths paths)
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SettingsPath => paths.SettingsPath;

    public static byte[] Serialize(AppSettings settings) => JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SettingsPath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(paths.SettingsPath);
        AppSettings settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AppSettings();
        return Migrate(settings);
    }

    internal static AppSettings Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return settings;
        }

        foreach (ModelPreference preference in settings.ModelPreferences.Values)
        {
            bool hasUsableContext = preference.LastLoadedContext is >= 2_048;
            bool matchesLegacyAutomatic = hasUsableContext &&
                preference.AutoCompactTokenLimit == ConfigurationSwitchService.SuggestLegacyAutoCompact(preference.LastLoadedContext!.Value);
            bool isAutomatic = preference.AutoCompactMode == AutoCompactMode.Automatic ||
                (preference.AutoCompactMode is null && (preference.AutoCompactTokenLimit is null || matchesLegacyAutomatic));

            preference.AutoCompactMode = isAutomatic ? AutoCompactMode.Automatic : AutoCompactMode.Manual;
            preference.AutoCompactPolicyVersion = ConfigurationSwitchService.AutoCompactPolicyVersion;
            if (hasUsableContext)
            {
                int contextWindow = preference.LastLoadedContext!.Value;
                if (isAutomatic)
                {
                    preference.AutoCompactTokenLimit = ConfigurationSwitchService.SuggestAutoCompact(contextWindow);
                }

                preference.ToolOutputTokenLimit = ConfigurationSwitchService.SuggestToolOutputLimit(contextWindow);
            }
        }

        settings.SchemaVersion = CurrentSchemaVersion;
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var bytes = Serialize(settings);
        var tempPath = paths.SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }

        if (File.Exists(paths.SettingsPath))
        {
            var rollback = paths.SettingsPath + ".rollback-" + Guid.NewGuid().ToString("N");
            File.Replace(tempPath, paths.SettingsPath, rollback, true);
            File.Delete(rollback);
        }
        else
        {
            File.Move(tempPath, paths.SettingsPath);
        }
    }
}
