using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Providers;

/// <summary>
/// DeepSeek 模型目录服务：优先复用官方安装脚本生成的 ~/.codex/models.json，
/// 否则下载官方 setup 脚本提取 ModelsJson（附 provenance 记录），
/// 网络不可用时回退到缓存，最后回退到程序集内嵌的官方快照。
/// 任何来源的 catalog 都必须通过严格的结构校验。
/// </summary>
public sealed partial class DeepSeekCatalogService : IModelCatalogService
{
    /// <summary>官方 DeepSeek setup 脚本地址。</summary>
    public const string OfficialScriptUrl = "https://cdn.deepseek.com/api-docs/codex-deepseek-setup-en.ps1";
    private const string SnapshotResourceSuffix = "Catalogs.deepseek-models.official-snapshot.json";
    private const string SnapshotProvenanceResourceSuffix = "Catalogs.deepseek-models.official-snapshot.provenance.json";
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] OfficialSlugs = ["deepseek-v4-flash", "deepseek-v4-pro"];

    private readonly ICodexHomeProvider homeProvider;
    private readonly AppPaths appPaths;
    private readonly HttpClient httpClient;

    /// <summary>注入主目录解析与应用路径构造；未提供 HttpClient 时默认 20 秒超时。</summary>
    public DeepSeekCatalogService(ICodexHomeProvider homeProvider, AppPaths appPaths, HttpClient? httpClient = null)
    {
        this.homeProvider = homeProvider;
        this.appPaths = appPaths;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>获取 DeepSeek 模型清单（确保 catalog 就绪后读取并解析）。</summary>
    public async Task<IReadOnlyList<ModelProfile>> GetDeepSeekModelsAsync(CancellationToken cancellationToken = default)
    {
        string path = await EnsureDeepSeekCatalogAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ValidateCatalog(bytes);
        return ParseModels(document.RootElement, path);
    }

    /// <summary>确保 catalog 就绪（按类说明的优先级），返回可用的 catalog 文件路径。</summary>
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
                // 损坏/无关的 models.json 绝不在此覆盖；改用隔离的 catalog 文件
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await RecoverCatalogAsync(cachedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException)
        {
            return await RecoverCatalogAsync(cachedPath, cancellationToken).ConfigureAwait(false);
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
                throw new InvalidDataException("DeepSeek catalog 根节点必须是对象并包含 models 数组。");
            }

            HashSet<string> slugs = new(StringComparer.Ordinal);
            foreach (JsonElement model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(model, "slug", out string slug) ||
                    !slugs.Add(slug) ||
                    !TryGetRequiredString(model, "minimal_client_version", out string minimum) ||
                    SemanticVersion.Parse(minimum) is null ||
                    !model.TryGetProperty("context_window", out JsonElement context) ||
                    context.ValueKind != JsonValueKind.Number ||
                    !context.TryGetInt32(out int contextValue) || contextValue <= 0)
                {
                    throw new InvalidDataException("DeepSeek catalog 包含非对象、重复 slug 或不完整的模型 metadata。");
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
                SupportsVision: supportsVision,
                ReasoningOptions: reasoning,
                Source: source,
                MinimalClientVersion: model.GetProperty("minimal_client_version").GetString(),
                DefaultReasoningEffort: model.TryGetProperty("default_reasoning_level", out JsonElement defaultReasoning) ? defaultReasoning.GetString() : null,
                ModelType: "llm"));
        }

        return result;
    }

    /// <summary>判断模型是否带完整的 Codex 工具 metadata（apply_patch_tool_type + shell_type）。</summary>
    private static bool HasCodexToolMetadata(JsonElement model) =>
        model.TryGetProperty("apply_patch_tool_type", out JsonElement patch) && patch.ValueKind == JsonValueKind.String &&
        model.TryGetProperty("shell_type", out JsonElement shell) && shell.ValueKind == JsonValueKind.String;

    /// <summary>判断 catalog 是否包含全部官方模型且其工具/推理 metadata 完整。</summary>
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

    /// <summary>离线恢复：缓存可用则用缓存，否则落盘内嵌官方快照（含 provenance）。</summary>
    private static async Task<string> RecoverCatalogAsync(string cachedPath, CancellationToken cancellationToken)
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

        byte[] snapshot = ReadEmbeddedSnapshot();
        using JsonDocument validatedSnapshot = ValidateCatalog(snapshot);
        await WriteCacheIfChangedAsync(cachedPath, snapshot, cancellationToken).ConfigureAwait(false);
        await WriteCacheIfChangedAsync(cachedPath + ".provenance.json", ReadEmbeddedResource(SnapshotProvenanceResourceSuffix), cancellationToken).ConfigureAwait(false);
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
            throw new InvalidDataException($"DeepSeek catalog 字段 {name} 必须是 string 或 null。");
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
            throw new InvalidDataException($"DeepSeek catalog 字段 {name} 必须是 string array 或 null。");
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
            throw new InvalidDataException("DeepSeek catalog supported_reasoning_levels 必须是 array 或 null。");
        }

        HashSet<string> efforts = new(StringComparer.Ordinal);
        foreach (JsonElement level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Object ||
                !TryGetRequiredString(level, "effort", out string effort) ||
                !efforts.Add(effort))
            {
                throw new InvalidDataException("DeepSeek catalog reasoning level 必须是 effort 唯一的对象。");
            }

            ValidateOptionalString(level, "description");
        }
    }

    /// <summary>用正则从官方 setup 脚本中提取 $ModelsJson here-string 的 JSON 内容。</summary>
    private static string ExtractCatalog(string script)
    {
        Match match = ModelsHereStringRegex().Match(script);
        if (!match.Success)
        {
            throw new InvalidDataException("官方 DeepSeek setup script 中未找到 ModelsJson here-string。");
        }

        return match.Groups["json"].Value;
    }

    /// <summary>读取内嵌的官方 catalog 快照。</summary>
    private static byte[] ReadEmbeddedSnapshot() => ReadEmbeddedResource(SnapshotResourceSuffix);

    /// <summary>按后缀定位并读取程序集内嵌资源。</summary>
    private static byte[] ReadEmbeddedResource(string suffix)
    {
        Assembly assembly = typeof(DeepSeekCatalogService).Assembly;
        string name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"内置资源缺失: {suffix}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
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

    /// <summary>写入 provenance 记录：脚本与 catalog 的来源、抓取时间和双 SHA-256。</summary>
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
        if (File.Exists(path))
        {
            File.Replace(temp, path, null, true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    // 匹配 PowerShell here-string 形式的 $ModelsJson = @' ... '@
    [GeneratedRegex("(?s)\\$ModelsJson\\s*=\\s*@'\\r?\\n(?<json>.*?)\\r?\\n'@", RegexOptions.CultureInvariant)]
    private static partial Regex ModelsHereStringRegex();
}
