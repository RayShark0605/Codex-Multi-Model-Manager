using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public sealed partial class DeepSeekCatalogService : IModelCatalogService
{
    public const string OfficialScriptUrl = "https://cdn.deepseek.com/api-docs/codex-deepseek-setup-en.ps1";
    private const string SnapshotResourceSuffix = "Catalogs.deepseek-models.official-snapshot.json";
    private const string SnapshotProvenanceResourceSuffix = "Catalogs.deepseek-models.official-snapshot.provenance.json";
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] OfficialSlugs = ["deepseek-v4-flash", "deepseek-v4-pro"];

    private readonly ICodexHomeProvider homeProvider;
    private readonly AppPaths appPaths;
    private readonly HttpClient httpClient;

    public DeepSeekCatalogService(ICodexHomeProvider homeProvider, AppPaths appPaths, HttpClient? httpClient = null)
    {
        this.homeProvider = homeProvider;
        this.appPaths = appPaths;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyList<ModelProfile>> GetDeepSeekModelsAsync(CancellationToken cancellationToken = default)
    {
        string path = await EnsureDeepSeekCatalogAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ValidateCatalog(bytes);
        return ParseModels(document.RootElement, path);
    }

    public async Task<string> EnsureDeepSeekCatalogAsync(CancellationToken cancellationToken = default)
    {
        string officialExisting = Path.Combine(homeProvider.GetCodexHome(), "models.json");
        if (File.Exists(officialExisting))
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(officialExisting, cancellationToken).ConfigureAwait(false);
                using JsonDocument existing = ValidateCatalog(bytes);
                if (ContainsOfficialModels(existing.RootElement))
                {
                    return officialExisting;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // A corrupt/unrelated models.json is never overwritten here; use an isolated catalog.
            }
        }

        appPaths.EnsureDirectories();
        string cachedPath = Path.Combine(appPaths.CatalogDirectory, "deepseek-models.json");
        try
        {
            string script = await httpClient.GetStringAsync(OfficialScriptUrl, cancellationToken).ConfigureAwait(false);
            string catalog = ExtractCatalog(script);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(catalog.TrimEnd('\r', '\n') + "\n");
            using JsonDocument validatedDownload = ValidateCatalog(bytes);
            await WriteCacheIfChangedAsync(cachedPath, bytes, cancellationToken).ConfigureAwait(false);
            await WriteProvenanceAsync(cachedPath, script, bytes, cancellationToken).ConfigureAwait(false);
            return cachedPath;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        {
            if (File.Exists(cachedPath))
            {
                try
                {
                    using JsonDocument validatedCache = ValidateCatalog(await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false));
                    return cachedPath;
                }
                catch (Exception cacheException) when (cacheException is IOException or JsonException or InvalidDataException)
                {
                }
            }

            byte[] snapshot = ReadEmbeddedSnapshot();
            using JsonDocument validatedSnapshot = ValidateCatalog(snapshot);
            await WriteCacheIfChangedAsync(cachedPath, snapshot, cancellationToken).ConfigureAwait(false);
            await WriteCacheIfChangedAsync(cachedPath + ".provenance.json", ReadEmbeddedResource(SnapshotProvenanceResourceSuffix), cancellationToken).ConfigureAwait(false);
            return cachedPath;
        }
    }

    public static JsonDocument ValidateCatalog(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new InvalidDataException("DeepSeek catalog 缺少 models 数组。");
        }

        foreach (JsonElement model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("slug", out JsonElement slug) || string.IsNullOrWhiteSpace(slug.GetString()) ||
                !model.TryGetProperty("minimal_client_version", out JsonElement minimum) || string.IsNullOrWhiteSpace(minimum.GetString()) ||
                !model.TryGetProperty("context_window", out JsonElement context) || !context.TryGetInt32(out int contextValue) || contextValue <= 0)
            {
                document.Dispose();
                throw new InvalidDataException("DeepSeek catalog 包含不完整的模型 metadata。");
            }
        }

        return document;
    }

    private static List<ModelProfile> ParseModels(JsonElement root, string source)
    {
        List<ModelProfile> result = [];
        foreach (JsonElement model in root.GetProperty("models").EnumerateArray())
        {
            string slug = model.GetProperty("slug").GetString()!;
            List<string> reasoning = [];
            if (model.TryGetProperty("supported_reasoning_levels", out JsonElement levels) && levels.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement level in levels.EnumerateArray())
                {
                    if (level.TryGetProperty("effort", out JsonElement effort) && effort.GetString() is string value) reasoning.Add(value);
                }
            }

            result.Add(new ModelProfile(
                slug,
                model.TryGetProperty("display_name", out JsonElement displayName) ? displayName.GetString() ?? slug : slug,
                ProviderKind.DeepSeek,
                model.TryGetProperty("description", out JsonElement description) ? description.GetString() : null,
                IsLoaded: true,
                MaxContextLength: model.GetProperty("context_window").GetInt32(),
                LoadedContextLength: model.GetProperty("context_window").GetInt32(),
                TrainedForToolUse: HasCodexToolMetadata(model) ? true : null,
                SupportsReasoning: reasoning.Count > 0,
                SupportsVision: model.TryGetProperty("input_modalities", out JsonElement modalities) && modalities.EnumerateArray().Any(item => item.GetString() == "image"),
                ReasoningOptions: reasoning,
                Source: source,
                MinimalClientVersion: model.GetProperty("minimal_client_version").GetString(),
                DefaultReasoningEffort: model.TryGetProperty("default_reasoning_level", out JsonElement defaultReasoning) ? defaultReasoning.GetString() : null,
                ModelType: "llm"));
        }

        return result;
    }

    private static bool HasCodexToolMetadata(JsonElement model) =>
        model.TryGetProperty("apply_patch_tool_type", out JsonElement patch) && patch.ValueKind == JsonValueKind.String &&
        model.TryGetProperty("shell_type", out JsonElement shell) && shell.ValueKind == JsonValueKind.String;

    private static bool ContainsOfficialModels(JsonElement root)
    {
        Dictionary<string, JsonElement> models = root.GetProperty("models").EnumerateArray()
            .Where(model => model.TryGetProperty("slug", out JsonElement slug) && slug.ValueKind == JsonValueKind.String)
            .ToDictionary(model => model.GetProperty("slug").GetString()!, model => model, StringComparer.Ordinal);
        foreach (string slug in OfficialSlugs)
        {
            if (!models.TryGetValue(slug, out JsonElement model) ||
                !model.TryGetProperty("apply_patch_tool_type", out JsonElement patch) || patch.GetString() != "freeform" ||
                !model.TryGetProperty("shell_type", out JsonElement shell) || shell.GetString() != "shell_command" ||
                !model.TryGetProperty("supported_reasoning_levels", out JsonElement reasoning) || reasoning.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
        }

        return true;
    }

    private static string ExtractCatalog(string script)
    {
        Match match = ModelsHereStringRegex().Match(script);
        if (!match.Success)
        {
            throw new InvalidDataException("官方 DeepSeek setup script 中未找到 ModelsJson here-string。");
        }

        return match.Groups["json"].Value;
    }

    private static byte[] ReadEmbeddedSnapshot() => ReadEmbeddedResource(SnapshotResourceSuffix);

    private static byte[] ReadEmbeddedResource(string suffix)
    {
        Assembly assembly = typeof(DeepSeekCatalogService).Assembly;
        string name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"内置资源缺失: {suffix}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static async Task WriteCacheIfChangedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            byte[] existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes))) return;
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }

        if (File.Exists(path))
        {
            string rollback = path + ".rollback-" + Guid.NewGuid().ToString("N");
            File.Replace(temp, path, rollback, true);
            File.Delete(rollback);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static async Task WriteProvenanceAsync(string catalogPath, string script, byte[] catalog, CancellationToken cancellationToken)
    {
        var data = new
        {
            source = OfficialScriptUrl,
            fetchedAt = DateTimeOffset.UtcNow,
            scriptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))),
            catalogSha256 = Convert.ToHexString(SHA256.HashData(catalog)),
        };
        string path = catalogPath + ".provenance.json";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, IndentedJson);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
    }

    [GeneratedRegex("(?s)\\$ModelsJson\\s*=\\s*@'\\r?\\n(?<json>.*?)\\r?\\n'@", RegexOptions.CultureInvariant)]
    private static partial Regex ModelsHereStringRegex();
}
