using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Infrastructure;

namespace CodexModelManager.Core.Providers;

internal static class ResponsesSseParser
{
    public static async Task<bool> HasValidEventAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var reader = new BoundedUtf8LineReader(stream, new ProcessOutputBudget(maximumBytes));
        var data = new StringBuilder();
        string? eventName = null;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0) return IsValidEvent(eventName, data.ToString());
                    eventName = null;
                    continue;
                }
                if (line[0] == ':') continue;
                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? string.Empty : line[(colon + 1)..];
                if (value.StartsWith(' ')) value = value[1..];
                if (field == "event") eventName = value;
                else if (field == "data") data.Append(value).Append('\n');
            }
            return false;
        }
        catch (ProcessOutputLimitException exception)
        {
            throw new InvalidDataException("SSE 在首个有效事件前超过响应上限。", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SSE 响应包含无效 UTF-8。", exception);
        }
    }

    private static bool IsValidEvent(string? eventName, string data)
    {
        if (data.Trim() == "[DONE]" || IsFailureType(eventName)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasError(root)) return false;
            string? type = root.TryGetProperty("type", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (IsFailureType(type)) return false;
            if (!string.IsNullOrEmpty(eventName) && !string.IsNullOrEmpty(type) && eventName != type) return false;
            if (root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object &&
                (HasError(response) || response.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String && status.GetString() == "failed")) return false;
            return (type ?? eventName)?.StartsWith("response.", StringComparison.Ordinal) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasError(JsonElement root) => root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null;

    private static bool IsFailureType(string? type) => type is "error" or "response.failed" or "response.error";
}
