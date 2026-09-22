using System.Diagnostics;
using System.Text.Json;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// Codex app-server 客户端：以“请求-响应”协议驱动 Codex CLI 的 app-server 子命令，
/// 获取模型清单与 Provider 能力；CLI 不可用时回退到本机缓存文件（models_cache.json / models.json）。
/// </summary>
public sealed class CodexAppServerClient
{
    private readonly CodexLaunchCommand? launchCommand;
    private readonly string codexHome;

    /// <summary>以 CODEX_HOME 与可选的显式可执行路径构造（未指定时自动探测）。</summary>
    public CodexAppServerClient(string codexHome, string? executablePath = null)
    {
        this.codexHome = codexHome;
        launchCommand = string.IsNullOrWhiteSpace(executablePath) ? CodexExecutableLocator.FindInvocation() : new CodexLaunchCommand(Path.GetFullPath(executablePath), [], "explicit executable");
    }

    /// <summary>测试用构造：直接传入已探测的启动命令（可为 null 表示 CLI 不可用）。</summary>
    internal CodexAppServerClient(string codexHome, CodexLaunchCommand? launchCommand)
    {
        this.codexHome = codexHome;
        this.launchCommand = launchCommand;
    }

    /// <summary>实际使用的 Codex 可执行路径。</summary>
    public string? ExecutablePath => launchCommand?.FileName;

    /// <summary>最近一次 model/list 成功后读到的 Provider 能力快照。</summary>
    public ProviderCapabilitySnapshot? LastCapabilities { get; private set; }

    /// <summary>
    /// 获取模型清单：优先实时询问 app-server；失败或结果为空时回退到本机缓存
    /// （两种路径都会把 LastCapabilities 清空，能力只在实时路径成功时设置）。
    /// </summary>
    public async Task<IReadOnlyList<ModelProfile>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        LastCapabilities = null;
        if (launchCommand is null)
        {
            return await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            IReadOnlyList<ModelProfile> live = await ListLiveAsync(cancellationToken).ConfigureAwait(false);
            if (live.Count > 0)
            {
                return live;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        LastCapabilities = null;
        return await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询 CLI 版本号（“--version”输出首行）；CLI 不可用或失败返回 null。</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (launchCommand is null)
        {
            return null;
        }

        try
        {
            ProcessStartInfo start = launchCommand.CreateStartInfo(["--version"]);
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(start, TimeSpan.FromSeconds(5), BoundedProcessRunner.StatusOutputLimit, BoundedProcessRunner.StatusOutputLimit, cancellationToken, combineOutputBudget: true).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 实时获取模型清单：初始化 app-server 会话（initialize → initialized），
    /// 调 model/list 解析模型，再以 2 秒预算尝试 modelProvider/capabilities/read 读取能力
    /// （旧版 app-server 可能没有该端点，失败不影响模型清单）。
    /// </summary>
    private async Task<IReadOnlyList<ModelProfile>> ListLiveAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo start = launchCommand!.CreateStartInfo(["app-server"]);
        start.WorkingDirectory = Environment.CurrentDirectory;
        start.Environment["CODEX_HOME"] = codexHome;
        return await BoundedProcessRunner.RunProtocolAsync(start, TimeSpan.FromSeconds(12), BoundedProcessRunner.CatalogOutputLimit, async (connection, token) =>
        {
            await WriteMessageAsync(connection, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "codex-model-manager", title = "Codex Multi-Model Manager", version = "1.0.0" },
                    capabilities = new { },
                },
            }, token).ConfigureAwait(false);
            await WaitForResponseAsync(connection, 1, token).ConfigureAwait(false);
            await WriteMessageAsync(connection, new { method = "initialized", @params = new { } }, token).ConfigureAwait(false);
            await WriteMessageAsync(connection, new { id = 2, method = "model/list", @params = new { includeHidden = false, limit = 100 } }, token).ConfigureAwait(false);
            JsonElement response = await WaitForResponseAsync(connection, 2, token).ConfigureAwait(false);
            List<ModelProfile> models = ParseAppServerModels(response);
            try
            {
                using var capabilityTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                capabilityTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await WriteMessageAsync(connection, new { id = 3, method = "modelProvider/capabilities/read", @params = new { } }, capabilityTimeout.Token).ConfigureAwait(false);
                JsonElement capabilities = await WaitForResponseAsync(connection, 3, capabilityTimeout.Token).ConfigureAwait(false);
                LastCapabilities = ParseProviderCapabilities(capabilities);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                // 旧版 app-server 可能不提供该端点：模型清单仍是权威结果，能力保持 Unknown/Untested
            }

            return (IReadOnlyList<ModelProfile>)models;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把消息序列化为 JSON 并按行写入连接。</summary>
    private static Task WriteMessageAsync<T>(BoundedProcessConnection connection, T message, CancellationToken cancellationToken) =>
        connection.WriteLineAsync(JsonSerializer.Serialize(message), cancellationToken);

    /// <summary>循环读取响应行，直到出现匹配 id 的响应；协议错误或缺少 result 即抛异常。</summary>
    private static async Task<JsonElement> WaitForResponseAsync(BoundedProcessConnection connection, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("Codex app-server 在返回结果前退出。");
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out JsonElement responseId) && MatchesResponseId(responseId, id))
            {
                if (root.TryGetProperty("error", out _))
                {
                    throw new InvalidOperationException("Codex app-server 返回协议错误。");
                }

                if (!root.TryGetProperty("result", out JsonElement result))
                {
                    throw new InvalidDataException("Codex app-server 响应缺少 result。");
                }

                return result.Clone();
            }
        }
    }

    /// <summary>判断响应 id（数字或数字字符串）是否等于期望值。</summary>
    internal static bool MatchesResponseId(JsonElement responseId, int expected)
    {
        if (responseId.ValueKind == JsonValueKind.Number)
        {
            return responseId.TryGetInt32(out int actual) && actual == expected;
        }

        return responseId.ValueKind == JsonValueKind.String && int.TryParse(responseId.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed == expected;
    }

    /// <summary>
    /// 解析 model/list 响应为模型档案：兼容 data/models/裸数组三种形态，
    /// 字段名兼容 camelCase 与 snake_case，仅保留 gpt 系 OpenAI 模型的常用能力映射。
    /// </summary>
    internal static List<ModelProfile> ParseAppServerModels(JsonElement result)
    {
        JsonElement array;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            array = data;
        }
        else if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
        {
            array = models;
        }
        else if (result.ValueKind == JsonValueKind.Array)
        {
            array = result;
        }
        else
        {
            return [];
        }

        List<ModelProfile> profiles = [];
        foreach (JsonElement model in array.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = GetString(model, "id") ?? GetString(model, "model") ?? GetString(model, "slug");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            List<string> reasoning = ReadReasoningOptions(model);
            profiles.Add(new ModelProfile(
                id,
                GetString(model, "displayName") ?? GetString(model, "display_name") ?? id,
                ProviderKind.OpenAI,
                GetString(model, "description"),
                IsLoaded: true,
                MaxContextLength: GetInt(model, "contextWindow") ?? GetInt(model, "context_window"),
                LoadedContextLength: GetInt(model, "contextWindow") ?? GetInt(model, "context_window"),
                TrainedForToolUse: true,
                SupportsReasoning: reasoning.Count > 0,
                SupportsVision: HasArrayValue(model, "inputModalities", "image") || HasArrayValue(model, "input_modalities", "image"),
                ReasoningOptions: reasoning,
                Source: "Codex app-server model/list",
                DefaultReasoningEffort: GetString(model, "defaultReasoningEffort") ?? GetString(model, "default_reasoning_level"),
                ModelType: "llm"));
        }

        return profiles;
    }

    /// <summary>读取本机模型缓存：仅接受 models 数组且条目为 gpt- 开头的模型，全部标记 IsStale。</summary>
    private async Task<IReadOnlyList<ModelProfile>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        string[] names = ["models_cache.json", "models.json"];
        foreach (string name in names)
        {
            string path = Path.Combine(codexHome, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var reader = new BoundedUtf8LineReader(stream, new ProcessOutputBudget(BoundedProcessRunner.CatalogOutputLimit));
                using JsonDocument document = JsonDocument.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<ModelProfile> result = [];
                bool restrictToGptPrefix = name.Equals("models.json", StringComparison.OrdinalIgnoreCase);
                foreach (JsonElement model in models.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? id = GetString(model, "slug");
                    if (string.IsNullOrWhiteSpace(id) || restrictToGptPrefix && !id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    List<string> reasoning = ReadReasoningOptions(model);
                    result.Add(new ModelProfile(id, GetString(model, "display_name") ?? id, ProviderKind.OpenAI, GetString(model, "description"), IsLoaded: true, MaxContextLength: GetInt(model, "context_window"), LoadedContextLength: GetInt(model, "context_window"), TrainedForToolUse: true, SupportsReasoning: reasoning.Count > 0, ReasoningOptions: reasoning, Source: $"{name}（可能过期）", IsStale: true, ModelType: "llm"));
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
            }
        }

        return [];
    }

    /// <summary>读取模型的推理选项（兼容 supportedReasoningEfforts / supported_reasoning_levels）。</summary>
    private static List<string> ReadReasoningOptions(JsonElement model)
    {
        string[] names = ["supportedReasoningEfforts", "supported_reasoning_levels"];
        foreach (string name in names)
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty(name, out JsonElement levels) || levels.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return levels.EnumerateArray().Select(level =>
            {
                if (level.ValueKind == JsonValueKind.String)
                {
                    return level.GetString();
                }

                return GetString(level, "reasoningEffort") ?? GetString(level, "effort");
            }).OfType<string>().ToList();
        }

        return [];
    }

    /// <summary>解析 capabilities/read 响应为能力快照。</summary>
    public static ProviderCapabilitySnapshot ParseProviderCapabilities(JsonElement result) => new(
        GetBool(result, "namespaceTools"),
        GetBool(result, "imageGeneration"),
        GetBool(result, "webSearch"),
        "Codex app-server modelProvider/capabilities/read");

    /// <summary>读取字符串属性；不存在或类型不符返回 null。</summary>
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>读取 int 属性；不存在或无法转为 int 返回 null。</summary>
    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : null;

    /// <summary>读取 bool 属性；不存在或类型不符返回 null。</summary>
    private static bool? GetBool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>判断数组属性中是否包含指定字符串值。</summary>
    private static bool HasArrayValue(JsonElement element, string name, string expected) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement values) && values.ValueKind == JsonValueKind.Array &&
        values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == expected);
}
