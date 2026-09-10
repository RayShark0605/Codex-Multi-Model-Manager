using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Infrastructure;

namespace CodexModelManager.Core.Providers;

/// <summary>
/// SSE（Server-Sent Events）流解析器：按行解析事件字段，
/// 判断流中是否出现至少一个“有效”的 Responses 协议事件。
/// </summary>
internal static class ResponsesSseParser
{
    /// <summary>逐行读取 SSE 流（带字节预算），在首个完整事件处判断其有效性；流结束无有效事件返回 false。</summary>
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
                    // 空行 = 事件边界：已有数据则就地判定，否则清空事件名继续
                    if (data.Length > 0)
                    {
                        return IsValidEvent(eventName, data.ToString());
                    }

                    eventName = null;
                    continue;
                }

                if (line[0] == ':')
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? string.Empty : line[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                if (field == "event")
                {
                    eventName = value;
                }
                else if (field == "data")
                {
                    data.Append(value).Append('\n');
                }
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

    /// <summary>
    /// 判定单个事件是否有效：data 为 [DONE] 或失败类型无效；
    /// JSON 须为无 error 的对象、事件名与 type 一致、response 未失败，且 type 以 “response.” 开头。
    /// </summary>
    private static bool IsValidEvent(string? eventName, string data)
    {
        if (data.Trim() == "[DONE]" || IsFailureType(eventName))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasError(root))
            {
                return false;
            }

            string? type = root.TryGetProperty("type", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (IsFailureType(type))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(eventName) && !string.IsNullOrEmpty(type) && eventName != type)
            {
                return false;
            }

            if (root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object &&
                (HasError(response) || response.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String && status.GetString() == "failed"))
            {
                return false;
            }

            return (type ?? eventName)?.StartsWith("response.", StringComparison.Ordinal) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>判断 JSON 对象是否带非 null 的 error 属性。</summary>
    private static bool HasError(JsonElement root) => root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null;

    /// <summary>判断事件类型是否属于失败类型。</summary>
    private static bool IsFailureType(string? type) => type is "error" or "response.failed" or "response.error";
}
