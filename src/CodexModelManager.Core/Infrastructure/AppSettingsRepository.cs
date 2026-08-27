using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public sealed class AppSettingsRepository
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly AppPaths paths;
    private readonly Func<string, CancellationToken, Task>? quarantineCompleted;

    public AppSettingsRepository(AppPaths paths)
        : this(paths, null)
    {
    }

    internal AppSettingsRepository(
        AppPaths paths,
        Func<string, CancellationToken, Task>? quarantineCompleted)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.quarantineCompleted = quarantineCompleted;
    }

    public string SettingsPath => paths.SettingsPath;

    public static byte[] Serialize(AppSettings settings) => JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadWithRecoveryAsync(cancellationToken).ConfigureAwait(false)).Settings;

    public async Task<AppSettingsLoadResult> LoadWithRecoveryAsync(CancellationToken cancellationToken = default)
    {
        return await LoadWithRecoveryCoreAsync(null, retriesRemaining: 3, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppSettingsLoadResult> LoadWithRecoveryCoreAsync(
        AppSettingsLoadResult? priorRecovery,
        int retriesRemaining,
        CancellationToken cancellationToken)
    {
        FileFingerprint beforeRead = await FileFingerprintService.CaptureAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        if (!beforeRead.Exists)
        {
            return priorRecovery ?? new AppSettingsLoadResult(new AppSettings());
        }

        byte[] bytes = await File.ReadAllBytesAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        FileFingerprint afterRead = await FileFingerprintService.CaptureAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        string bytesSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!FileFingerprintService.Matches(beforeRead, afterRead) ||
            !string.Equals(beforeRead.Sha256, bytesSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (retriesRemaining <= 0)
            {
                throw new IOException("appsettings.json 在读取期间持续变化；为避免错误恢复，加载已中止。");
            }

            return await LoadWithRecoveryCoreAsync(priorRecovery, retriesRemaining - 1, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(bytes, JsonOptions)
                ?? throw new InvalidDataException("appsettings.json 根值不能为 null。");
            ValidateShape(settings);
            return priorRecovery is null
                ? new AppSettingsLoadResult(Migrate(settings))
                : priorRecovery with { Settings = Migrate(settings) };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            string sha256 = bytesSha256;
            string quarantinePath = await WriteQuarantineAsync(bytes, sha256, cancellationToken).ConfigureAwait(false);
            if (quarantineCompleted is not null)
            {
                await quarantineCompleted(quarantinePath, cancellationToken).ConfigureAwait(false);
            }

            var recovery = new AppSettingsLoadResult(
                new AppSettings(),
                quarantinePath,
                sha256,
                $"检测到损坏的 appsettings.json，原始字节已隔离到 {quarantinePath}，本次使用默认设置。",
                exception.GetType().Name);

            FileFingerprint currentFingerprint = await FileFingerprintService.CaptureAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
            if (!currentFingerprint.Exists)
            {
                return recovery;
            }

            if (FileFingerprintService.Matches(beforeRead, currentFingerprint))
            {
                File.Delete(paths.SettingsPath);
                return recovery;
            }

            if (retriesRemaining <= 0)
            {
                throw new IOException("appsettings.json 在损坏设置隔离期间持续变化；为避免删除并发写入，恢复已中止。", exception);
            }

            return await LoadWithRecoveryCoreAsync(recovery, retriesRemaining - 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> WriteQuarantineAsync(byte[] bytes, string expectedSha256, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(paths.Root, $"appsettings.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            byte[] persisted = await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
            string actualSha256 = Convert.ToHexString(SHA256.HashData(persisted));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedSha256),
                    Convert.FromHexString(actualSha256)))
            {
                throw new IOException("损坏设置隔离文件的 SHA-256 复核失败。");
            }

            File.Move(temporary, path);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateShape(AppSettings settings)
    {
        if (settings.SchemaVersion < 0 ||
            string.IsNullOrWhiteSpace(settings.LmStudioEndpoint) ||
            settings.ModelPreferences is null ||
            settings.ModelPreferences.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) ||
            settings.ProviderStates is null ||
            settings.ProviderStates.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null ||
                pair.Value.RootValues is null ||
                pair.Value.TableBodies is null ||
                string.IsNullOrWhiteSpace(pair.Value.SourceConfigSha256) ||
                !Enum.IsDefined(pair.Value.Provider)) ||
            settings.SecondaryOverrideOriginals is null ||
            settings.SecondaryOverrideOriginals.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new InvalidDataException("appsettings.json 包含缺失或无效的必要集合。");
        }
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
