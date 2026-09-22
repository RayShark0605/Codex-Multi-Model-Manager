using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

/// <summary>GLM 双平台的常量与内嵌资源访问：Provider ID、基础 URL、官方文档地址、缓存文件名与内嵌快照。</summary>
public static class GlmPlatforms
{
    /// <summary>两个平台共用的官方 Codex 指南与 Provider 表 ID。</summary>
    public const string ProviderId = "ZAI";

    /// <summary>Provider 表名：model_providers.ZAI。</summary>
    public const string ProviderTableName = "model_providers." + ProviderId;

    /// <summary>平台对应的 API 基础 URL。</summary>
    public static string BaseUrl(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "https://open.bigmodel.cn/api/v1",
        GlmPlatform.Zai => "https://api.z.ai/api/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    /// <summary>平台对应的官方 Codex 指南地址（models.json 的发布位置）。</summary>
    public static string OfficialDocsUrl(GlmPlatform platform) => platform switch
    {
        GlmPlatform.BigModel => "https://docs.bigmodel.cn/cn/coding-plan/tool/codex.md",
        GlmPlatform.Zai => "https://docs.z.ai/devpack/tool/codex.md",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "unknown GLM platform"),
    };

    /// <summary>平台对应的缓存文件名。</summary>
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

    /// <summary>读取平台的内嵌官方 catalog 快照。</summary>
    internal static byte[] ReadEmbeddedSnapshot(GlmPlatform platform) => ReadEmbeddedResource(SnapshotResourceSuffix(platform));

    /// <summary>读取平台的内嵌 provenance 记录。</summary>
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

/// <summary>
/// GLM 模型目录服务：优先复用官方指南生成的 ~/.codex/models.json，
/// 否则下载平台对应的官方 Codex 指南并从 ```json 代码块中提取 models.json（附 provenance），
/// 网络不可用时回退到缓存，最后回退到程序集内嵌的官方快照。
/// </summary>
public sealed partial class GlmCatalogService : IGlmModelCatalogService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly ICodexHomeProvider homeProvider;
    private readonly AppPaths appPaths;
    private readonly HttpClient httpClient;

    /// <summary>注入主目录解析与应用路径构造；未提供 HttpClient 时默认 20 秒超时。</summary>
    public GlmCatalogService(ICodexHomeProvider homeProvider, AppPaths appPaths, HttpClient? httpClient = null)
    {
        this.homeProvider = homeProvider;
        this.appPaths = appPaths;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>获取平台对应的 GLM 模型清单。</summary>
    public async Task<IReadOnlyList<ModelProfile>> GetGlmModelsAsync(GlmPlatform platform, CancellationToken cancellationToken = default)
    {
        string path = await EnsureGlmCatalogAsync(platform, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ValidateCatalog(bytes);
        return ParseModels(document.RootElement, path);
    }

    /// <summary>确保平台 catalog 就绪（按类说明的优先级），返回可用的 catalog 文件路径。</summary>
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
                // 损坏/无关的 models.json 绝不在此覆盖；改用隔离的 catalog 文件
            }
        }

        appPaths.EnsureDirectories();
        string cachedPath = Path.Combine(appPaths.CatalogDirectory, GlmPlatforms.CacheFileName(platform));
        try
        {
            // 官方 models.json 只发布在 GLM Coding Plan 的 Codex 指南 markdown 里；
            // 与 DeepSeek 的 setup 脚本一样逐字提取。
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

    /// <summary>严格校验 catalog 结构：根对象 + models 数组 + 每个模型的必填字段形状；失败即抛异常。</summary>
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

                // 官方 GLM models.json 不带 minimal_client_version；仅在字段存在时校验其可解析
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

    /// <summary>把已校验的 catalog 根节点解析为模型档案列表。</summary>
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
                    if (level.TryGetProperty("effort", out JsonElement effort) && effort.GetString() is string value)
                    {
                        reasoning.Add(value);
                    }
                }
            }

            bool supportsVision = model.TryGetProperty("input_modalities", out JsonElement modalities) &&
                modalities.ValueKind == JsonValueKind.Array &&
                modalities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "image");
            string? minimalClientVersion = model.TryGetProperty("minimal_client_version", out JsonElement minimum) && minimum.ValueKind == JsonValueKind.String ? minimum.GetString() : null;
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

    /// <summary>判断模型是否带完整的 Codex 工具 metadata（apply_patch_tool_type + shell_type）。</summary>
    private static bool HasCodexToolMetadata(JsonElement model) =>
        model.TryGetProperty("apply_patch_tool_type", out JsonElement patch) && patch.ValueKind == JsonValueKind.String &&
        model.TryGetProperty("shell_type", out JsonElement shell) && shell.ValueKind == JsonValueKind.String;

    /// <summary>判断 catalog 是否包含官方锚点模型（glm-5.3）且其工具/推理 metadata 完整。</summary>
    private static bool ContainsOfficialModels(JsonElement root)
    {
        Dictionary<string, JsonElement> models = root.GetProperty("models").EnumerateArray()
            .Where(model => model.TryGetProperty("slug", out JsonElement slug) && slug.ValueKind == JsonValueKind.String)
            .ToDictionary(model => model.GetProperty("slug").GetString()!, model => model, StringComparer.Ordinal);
        // glm-5.3 在两个平台的官方 Codex 指南中都有声明，用它锚定“是官方 catalog”的判定
        if (!models.TryGetValue("glm-5.3", out JsonElement model) ||
            !model.TryGetProperty("apply_patch_tool_type", out JsonElement patch) || patch.GetString() != "freeform" ||
            !model.TryGetProperty("shell_type", out JsonElement shell) || shell.GetString() != "shell_command" ||
            !model.TryGetProperty("supported_reasoning_levels", out JsonElement reasoning) || reasoning.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return true;
    }

    /// <summary>离线恢复：缓存可用则用缓存，否则落盘内嵌官方快照（含 provenance）。</summary>
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

    /// <summary>读取必填字符串字段：存在、为字符串且非空白。</summary>
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

    /// <summary>校验可选字段必须是 string 或 null。</summary>
    private static void ValidateOptionalString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new InvalidDataException($"GLM catalog 字段 {name} 必须是 string 或 null。");
        }
    }

    /// <summary>校验可选字段必须是 string 数组或 null。</summary>
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

    /// <summary>校验 supported_reasoning_levels：数组内必须是 effort 唯一的对象，可带 description。</summary>
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

    /// <summary>从指南 markdown 中找到含 slug 与 glm- 前缀的 ```json 代码块并原样返回。</summary>
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

    /// <summary>内容有变化时原子写缓存（临时文件 + Replace/Move）；内容相同则跳过写入。</summary>
    private static async Task WriteCacheIfChangedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            byte[] existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
            {
                return;
            }
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
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
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>写入 provenance 记录：指南与 catalog 的来源、抓取时间和双 SHA-256。</summary>
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
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // 匹配 markdown 中的 ```json 代码块
    [GeneratedRegex("(?s)```json[^\\r\\n]*\\r?\\n(?<json>.*?)[ \\t]*\\r?\\n[ \\t]*```", RegexOptions.CultureInvariant)]
    private static partial Regex JsonCodeBlockRegex();
}
