using System.Text.Json;
using System.Text.Json.Serialization;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public sealed class AppSettingsRepository(AppPaths paths)
{
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
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AppSettings();
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
