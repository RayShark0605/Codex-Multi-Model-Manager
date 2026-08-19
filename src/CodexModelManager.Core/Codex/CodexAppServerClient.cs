using System.Diagnostics;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

public sealed class CodexAppServerClient
{
    private readonly string? executablePath;
    private readonly string codexHome;

    public CodexAppServerClient(string codexHome, string? executablePath = null)
    {
        this.codexHome = codexHome;
        this.executablePath = executablePath ?? CodexExecutableLocator.Find();
    }

    public string? ExecutablePath => executablePath;

    public ProviderCapabilitySnapshot? LastCapabilities { get; private set; }

    public async Task<IReadOnlyList<ModelProfile>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        LastCapabilities = null;
        if (string.IsNullOrWhiteSpace(executablePath)) return await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ModelProfile> live = await ListLiveAsync(cancellationToken).ConfigureAwait(false);
            if (live.Count > 0) return live;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
        }

        return await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        try
        {
            var start = new ProcessStartInfo(executablePath, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Codex CLI。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return output.Trim();
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ModelProfile>> ListLiveAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath!, "app-server")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        start.Environment["CODEX_HOME"] = codexHome;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Codex app-server。");
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await WriteMessageAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "codex-model-manager", title = "Codex Multi-Model Manager", version = "1.0.0" },
                    capabilities = new { },
                },
            }).ConfigureAwait(false);
            await WaitForResponseAsync(process, 1, timeout.Token).ConfigureAwait(false);
            await WriteMessageAsync(process, new { method = "initialized", @params = new { } }).ConfigureAwait(false);
            await WriteMessageAsync(process, new { id = 2, method = "model/list", @params = new { includeHidden = false, limit = 100 } }).ConfigureAwait(false);
            JsonElement response = await WaitForResponseAsync(process, 2, timeout.Token).ConfigureAwait(false);
            List<ModelProfile> models = ParseAppServerModels(response);
            try
            {
                await WriteMessageAsync(process, new { id = 3, method = "modelProvider/capabilities/read", @params = new { } }).ConfigureAwait(false);
                JsonElement capabilities = await WaitForResponseAsync(process, 3, timeout.Token).ConfigureAwait(false);
                LastCapabilities = ParseProviderCapabilities(capabilities);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                // Older app-server builds may not expose this endpoint. The model list is
                // still authoritative; unsupported capabilities remain Unknown/Untested.
            }

            return models;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await stderrTask.ConfigureAwait(false);
        }
    }

    private static async Task WriteMessageAsync<T>(Process process, T message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<JsonElement> WaitForResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new IOException("Codex app-server 在返回结果前退出。");
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out JsonElement responseId) && responseId.TryGetInt32(out int actual) && actual == id)
            {
                if (document.RootElement.TryGetProperty("error", out JsonElement error)) throw new InvalidOperationException("Codex app-server error: " + error.GetRawText());
                return document.RootElement.GetProperty("result").Clone();
            }
        }
    }

    private static List<ModelProfile> ParseAppServerModels(JsonElement result)
    {
        JsonElement array;
        if (result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array) array = data;
        else if (result.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array) array = models;
        else if (result.ValueKind == JsonValueKind.Array) array = result;
        else return [];

        List<ModelProfile> profiles = [];
        foreach (JsonElement model in array.EnumerateArray())
        {
            string? id = GetString(model, "id") ?? GetString(model, "model") ?? GetString(model, "slug");
            if (string.IsNullOrWhiteSpace(id)) continue;
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

    private async Task<IReadOnlyList<ModelProfile>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        string[] names = ["models_cache.json", "models.json"];
        foreach (string name in names)
        {
            string path = Path.Combine(codexHome, name);
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array) continue;
                List<ModelProfile> result = [];
                foreach (JsonElement model in models.EnumerateArray())
                {
                    string? id = GetString(model, "slug");
                    if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) continue;
                    List<string> reasoning = ReadReasoningOptions(model);
                    result.Add(new ModelProfile(id, GetString(model, "display_name") ?? id, ProviderKind.OpenAI, GetString(model, "description"), IsLoaded: true, MaxContextLength: GetInt(model, "context_window"), LoadedContextLength: GetInt(model, "context_window"), TrainedForToolUse: true, SupportsReasoning: reasoning.Count > 0, ReasoningOptions: reasoning, Source: $"{name}（可能过期）", IsStale: true, ModelType: "llm"));
                }

                if (result.Count > 0) return result;
            }
            catch (JsonException)
            {
            }
        }

        return [];
    }

    private static List<string> ReadReasoningOptions(JsonElement model)
    {
        string[] names = ["supportedReasoningEfforts", "supported_reasoning_levels"];
        foreach (string name in names)
        {
            if (!model.TryGetProperty(name, out JsonElement levels) || levels.ValueKind != JsonValueKind.Array) continue;
            return levels.EnumerateArray().Select(level =>
            {
                if (level.ValueKind == JsonValueKind.String) return level.GetString();
                return GetString(level, "reasoningEffort") ?? GetString(level, "effort");
            }).OfType<string>().ToList();
        }

        return [];
    }

    public static ProviderCapabilitySnapshot ParseProviderCapabilities(JsonElement result) => new(
        GetBool(result, "namespaceTools"),
        GetBool(result, "imageGeneration"),
        GetBool(result, "webSearch"),
        "Codex app-server modelProvider/capabilities/read");

    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetInt(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : null;
    private static bool? GetBool(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static bool HasArrayValue(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out JsonElement values) && values.ValueKind == JsonValueKind.Array &&
        values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == expected);
}
