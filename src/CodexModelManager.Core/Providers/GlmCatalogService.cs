using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

public static class GlmPlatforms
{
    // Official Codex guide and provider table id used by both platforms.
    public const string ProviderId = "ZAI";
    public const string ProviderTableName = "model_providers." + ProviderId;

    public static string BaseUrl(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "https://open.bigmodel.cn/api/v1",
        GlmPlatform.Zai => "https://api.z.ai/api/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    public static string OfficialDocsUrl(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "https://docs.bigmodel.cn/cn/coding-plan/tool/codex.md",
        GlmPlatform.Zai => "https://docs.z.ai/devpack/tool/codex.md",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    public static string CacheFileName(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "glm-bigmodel-models.json",
        GlmPlatform.Zai => "glm-zai-models.json",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    private static string SnapshotResourceSuffix(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "Catalogs.glm-bigmodel-models.official-snapshot.json",
        GlmPlatform.Zai => "Catalogs.glm-zai-models.official-snapshot.json",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    private static string SnapshotProvenanceResourceSuffix(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "Catalogs.glm-bigmodel-models.official-snapshot.provenance.json",
        GlmPlatform.Zai => "Catalogs.glm-zai-models.official-snapshot.provenance.json",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    internal static byte[] ReadEmbeddedSnapshot(GlmPlatform platform) => ReadEmbeddedResource(SnapshotResourceSuffix(platform));

    internal static byte[] ReadEmbeddedProvenance(GlmPlatform platform) => ReadEmbeddedResource(SnapshotProvenanceResourceSuffix(platform));

    private static byte[] ReadEmbeddedResource(string suffix)
    {
        Assembly assembly = typeof(GlmCatalogService).Assembly;
        string name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"内置资源缺失: {suffix}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

public sealed partial class GlmCatalogService : IGlmModelCatalogService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly ICodexHomeProvider homeProvider;
    private readonly AppPaths appPaths;
    private readonly HttpClient httpClient;

    public GlmCatalogService(ICodexHomeProvider homeProvider, AppPaths appPaths, HttpClient? httpClient = null)
    {
        this.homeProvider = homeProvider;
        this.appPaths = appPaths;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyList<ModelProfile>> GetGlmModelsAsync(GlmPlatform platform, CancellationToken cancellationToken = default)
    {
        string path = await EnsureGlmCatalogAsync(platform, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ValidateCatalog(bytes);
        return ParseModels(document.RootElement, path);
    }

    public async Task<string> EnsureGlmCatalogAsync(GlmPlatform platform, CancellationToken cancellationToken = default)
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
        string cachedPath = Path.Combine(appPaths.CatalogDirectory, GlmPlatforms.CacheFileName(platform));
        try
        {
            // The official models.json is published only inside the GLM Coding Plan
            // Codex guide markdown; extract it verbatim like the DeepSeek setup script.
            string document = await httpClient.GetStringAsync(GlmPlatforms.OfficialDocsUrl(platform), cancellationToken).ConfigureAwait(false);
            string catalog = ExtractCatalog(document);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(catalog.TrimEnd('\r', '\n') + "\n");
            using JsonDocument validatedDownload = ValidateCatalog(bytes);
            await WriteCacheIfChangedAsync(cachedPath, bytes, cancellationToken).ConfigureAwait(false);
            await WriteProvenanceAsync(cachedPath, GlmPlatforms.OfficialDocsUrl(platform), document, bytes, cancellationToken).ConfigureAwait(false);
            return cachedPath;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await RecoverCatalogAsync(platform, cachedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException)
        {
            return await RecoverCatalogAsync(platform, cachedPath, cancellationToken).ConfigureAwait(false);
        }
    }

    public static JsonDocument ValidateCatalog(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument document = JsonDocument.Parse(bytes);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("GLM catalog 根节点必须是对象并包含 models 数组。");
            }

            HashSet<string> slugs = new(StringComparer.Ordinal);
            foreach (JsonElement model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(model, "slug", out string slug) ||
                    !slugs.Add(slug) ||
                    !model.TryGetProperty("context_window", out JsonElement context) ||
                    context.ValueKind != JsonValueKind.Number ||
                    !context.TryGetInt32(out int contextValue) || contextValue <= 0)
                {
                    throw new InvalidDataException("GLM catalog 包含非对象、重复 slug 或不完整的模型 metadata。");
                }

                // GLM official models.json has no minimal_client_version; accept it only when valid.
                if (model.TryGetProperty("minimal_client_version", out JsonElement minimum) && minimum.ValueKind != JsonValueKind.Null)
                {
                    if (minimum.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(minimum.GetString()) ||
                        SemanticVersion.Parse(minimum.GetString()!) is null)
                    {
                        throw new InvalidDataException("GLM catalog 字段 minimal_client_version 必须是可解析的语义版本或 null。");
                    }
                }

                ValidateOptionalString(model, "display_name");
                ValidateOptionalString(model, "description");
                ValidateOptionalString(model, "default_reasoning_level");
                ValidateOptionalString(model, "apply_patch_tool_type");
                ValidateOptionalString(model, "shell_type");
                ValidateOptionalStringArray(model, "input_modalities");
                ValidateReasoningLevels(model);
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
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

            bool supportsVision = model.TryGetProperty("input_modalities", out JsonElement modalities) &&
                modalities.ValueKind == JsonValueKind.Array &&
                modalities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "image");
            string? minimalClientVersion = model.TryGetProperty("minimal_client_version", out JsonElement minimum) && minimum.ValueKind == JsonValueKind.String
                ? minimum.GetString()
                : null;
            result.Add(new ModelProfile(
                slug,
                model.TryGetProperty("display_name", out JsonElement displayName) ? displayName.GetString() ?? slug : slug,
                ProviderKind.GLM,
                model.TryGetProperty("description", out JsonElement description) ? description.GetString() : null,
                IsLoaded: true,
                MaxContextLength: model.GetProperty("context_window").GetInt32(),
                LoadedContextLength: model.GetProperty("context_window").GetInt32(),
                TrainedForToolUse: HasCodexToolMetadata(model) ? true : null,
                SupportsReasoning: reasoning.Count > 0,
                SupportsVision: supportsVision,
                ReasoningOptions: reasoning,
                Source: source,
                MinimalClientVersion: minimalClientVersion,
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
        // glm-5.3 is declared by both platforms' official Codex guides and anchors the check.
        if (!models.TryGetValue("glm-5.3", out JsonElement model) ||
            !model.TryGetProperty("apply_patch_tool_type", out JsonElement patch) || patch.GetString() != "freeform" ||
            !model.TryGetProperty("shell_type", out JsonElement shell) || shell.GetString() != "shell_command" ||
            !model.TryGetProperty("supported_reasoning_levels", out JsonElement reasoning) || reasoning.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return true;
    }

    private static async Task<string> RecoverCatalogAsync(GlmPlatform platform, string cachedPath, CancellationToken cancellationToken)
    {
        if (File.Exists(cachedPath))
        {
            try
            {
                using JsonDocument validatedCache = ValidateCatalog(await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false));
                return cachedPath;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
            }
        }

        byte[] snapshot = GlmPlatforms.ReadEmbeddedSnapshot(platform);
        using JsonDocument validatedSnapshot = ValidateCatalog(snapshot);
        await WriteCacheIfChangedAsync(cachedPath, snapshot, cancellationToken).ConfigureAwait(false);
        await WriteCacheIfChangedAsync(cachedPath + ".provenance.json", GlmPlatforms.ReadEmbeddedProvenance(platform), cancellationToken).ConfigureAwait(false);
        return cachedPath;
    }

    private static bool TryGetRequiredString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void ValidateOptionalString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new InvalidDataException($"GLM catalog 字段 {name} 必须是 string 或 null。");
        }
    }

    private static void ValidateOptionalStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException($"GLM catalog 字段 {name} 必须是 string array 或 null。");
        }
    }

    private static void ValidateReasoningLevels(JsonElement model)
    {
        if (!model.TryGetProperty("supported_reasoning_levels", out JsonElement levels) || levels.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (levels.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GLM catalog supported_reasoning_levels 必须是 array 或 null。");
        }

        HashSet<string> efforts = new(StringComparer.Ordinal);
        foreach (JsonElement level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Object ||
                !TryGetRequiredString(level, "effort", out string effort) ||
                !efforts.Add(effort))
            {
                throw new InvalidDataException("GLM catalog reasoning level 必须是 effort 唯一的对象。");
            }

            ValidateOptionalString(level, "description");
        }
    }

    private static string ExtractCatalog(string document)
    {
        foreach (Match match in JsonCodeBlockRegex().Matches(document))
        {
            string candidate = match.Groups["json"].Value;
            if (candidate.Contains("\"slug\"", StringComparison.Ordinal) && candidate.Contains("\"glm-", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidDataException("官方 GLM Codex 指南中未找到 models.json 代码块。");
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

    private static async Task WriteProvenanceAsync(string catalogPath, string docsUrl, string document, byte[] catalog, CancellationToken cancellationToken)
    {
        var data = new
        {
            source = docsUrl,
            fetchedAt = DateTimeOffset.UtcNow,
            docSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))),
            catalogSha256 = Convert.ToHexString(SHA256.HashData(catalog)),
        };
        string path = catalogPath + ".provenance.json";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, IndentedJson);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
    }

    [GeneratedRegex("(?s)```json[^\\r\\n]*\\r?\\n(?<json>.*?)[ \\t]*\\r?\\n[ \\t]*```", RegexOptions.CultureInvariant)]
    private static partial Regex JsonCodeBlockRegex();
}
