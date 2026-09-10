using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// appsettings.json 仓库：负责应用设置的读取、损坏恢复（隔离 + 默认值兜底）、
/// 模式迁移与原子保存（临时文件 + File.Replace）。
/// </summary>
public sealed class AppSettingsRepository
{
    /// <summary>当前设置模式版本；旧版本在加载时迁移到该版本。</summary>
    public const int CurrentSchemaVersion = 2;

    // 序列化选项：缩进输出、camelCase 命名、枚举以字符串表示
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths paths;
    private readonly Func<string, CancellationToken, Task>? quarantineCompleted;

    /// <summary>常规构造：不注册隔离完成回调。</summary>
    public AppSettingsRepository(AppPaths paths)
        : this(paths, null)
    {
    }

    /// <summary>测试用构造：<paramref name="quarantineCompleted"/> 在损坏设置隔离完成后被调用。</summary>
    internal AppSettingsRepository(AppPaths paths, Func<string, CancellationToken, Task>? quarantineCompleted)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.quarantineCompleted = quarantineCompleted;
    }

    /// <summary>设置文件完整路径。</summary>
    public string SettingsPath => paths.SettingsPath;

    /// <summary>把设置序列化为 UTF-8 字节（供保存与测试使用）。</summary>
    public static byte[] Serialize(AppSettings settings) => JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);

    /// <summary>加载设置（含损坏恢复与迁移）。</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => (await LoadWithRecoveryAsync(cancellationToken).ConfigureAwait(false)).Settings;

    /// <summary>加载设置并返回恢复详情（是否发生隔离、隔离路径、原始哈希等）。</summary>
    public async Task<AppSettingsLoadResult> LoadWithRecoveryAsync(CancellationToken cancellationToken = default)
    {
        return await LoadWithRecoveryCoreAsync(null, retriesRemaining: 3, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 加载核心：读取前后各取一次指纹并在读后复核字节哈希，确保文件在读取期间未被并发修改；
    /// 解析失败时把原始字节隔离到独立文件并以默认设置继续（最多重试 3 次）。
    /// </summary>
    private async Task<AppSettingsLoadResult> LoadWithRecoveryCoreAsync(AppSettingsLoadResult? priorRecovery, int retriesRemaining, CancellationToken cancellationToken)
    {
        FileFingerprint beforeRead = await FileFingerprintService.CaptureAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        if (!beforeRead.Exists)
        {
            return priorRecovery ?? new AppSettingsLoadResult(new AppSettings());
        }

        byte[] bytes = await File.ReadAllBytesAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        FileFingerprint afterRead = await FileFingerprintService.CaptureAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        string bytesSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!FileFingerprintService.Matches(beforeRead, afterRead) || !string.Equals(beforeRead.Sha256, bytesSha256, StringComparison.OrdinalIgnoreCase))
        {
            // 文件在读取期间被改动：重读，直到重试耗尽后放弃以避免基于脏数据恢复
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
            try
            {
                settings.SecondaryOverrideOriginals = new Dictionary<string, string>(settings.SecondaryOverrideOriginals, SecondaryOverrideKeyComparer.Instance);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("appsettings.json 包含重复的 Secondary Override 原始值键。");
            }

            return priorRecovery is null ? new AppSettingsLoadResult(Migrate(settings)) : priorRecovery with { Settings = Migrate(settings) };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // 设置损坏：隔离原始字节，返回默认设置的恢复结果
            string sha256 = bytesSha256;
            string quarantinePath = await WriteQuarantineAsync(bytes, sha256, cancellationToken).ConfigureAwait(false);
            if (quarantineCompleted is not null)
            {
                await quarantineCompleted(quarantinePath, cancellationToken).ConfigureAwait(false);
            }

            var recovery = new AppSettingsLoadResult(new AppSettings(), quarantinePath, sha256, $"检测到损坏的 appsettings.json，原始字节已隔离到 {quarantinePath}，本次使用默认设置。", exception.GetType().Name);

            // 隔离期间原文件可能又被写入：仅在指纹未变时删除，否则带着恢复结果重读
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

    /// <summary>
    /// 把损坏设置的原始字节写入隔离文件：先落临时文件并读回复核 SHA-256（固定时间比较），
    /// 确认无误后原子改名，任何情况下都清理临时文件。
    /// </summary>
    private async Task<string> WriteQuarantineAsync(byte[] bytes, string expectedSha256, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(paths.Root, $"appsettings.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            byte[] persisted = await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
            string actualSha256 = Convert.ToHexString(SHA256.HashData(persisted));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedSha256), Convert.FromHexString(actualSha256)))
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

    /// <summary>校验反序列化结果的结构完整性：版本号、端点与三个必要集合的形状。</summary>
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

    /// <summary>
    /// 把旧模式设置迁移到当前版本（v2）：推导每个模型偏好的 AutoCompact 模式，
    /// 并按当前策略重算自动压缩阈值与工具输出上限，最后提升模式版本号。
    /// </summary>
    internal static AppSettings Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return settings;
        }

        foreach (ModelPreference preference in settings.ModelPreferences.Values)
        {
            bool hasUsableContext = preference.LastLoadedContext is >= 2_048;
            bool matchesLegacyAutomatic = hasUsableContext && preference.AutoCompactTokenLimit == ConfigurationSwitchService.SuggestLegacyAutoCompact(preference.LastLoadedContext!.Value);
            bool isAutomatic = preference.AutoCompactMode == AutoCompactMode.Automatic || (preference.AutoCompactMode is null && (preference.AutoCompactTokenLimit is null || matchesLegacyAutomatic));

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

    /// <summary>
    /// 原子保存：先写临时文件（WriteThrough 落盘），已存在正式文件时用 File.Replace 保留回滚副本后删除，
    /// 不存在时直接改名，保证任何时刻磁盘上都是完整的一份设置。
    /// </summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var bytes = Serialize(settings);
        var tempPath = paths.SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
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
