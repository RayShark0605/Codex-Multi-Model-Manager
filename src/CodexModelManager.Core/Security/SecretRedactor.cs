using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CodexModelManager.Core.Security;

/// <summary>
/// 敏感信息脱敏器：先登记运行期已知的密钥明文，输出日志时按“已知密钥替换 +
/// 常见 Authorization/TOML/JSON/API Key/URL 查询参数模式正则”双层兜底，保证日志不泄露机密。
/// </summary>
public sealed partial class SecretRedactor
{
    // 已登记的密钥集合；值无意义，仅用 ConcurrentDictionary 做线程安全集合
    private readonly ConcurrentDictionary<string, byte> knownSecrets = new(StringComparer.Ordinal);

    /// <summary>登记一个需要从输出中抹除的密钥；空白或过短（&lt;4 字符）的值忽略。</summary>
    public void Register(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4)
        {
            knownSecrets.TryAdd(secret, 0);
        }
    }

    /// <summary>
    /// 脱敏一段文本：先按长度降序替换全部已登记密钥（长的优先，避免部分匹配留下残段），
    /// 再跑五个模式正则兜底；任一正则超时则整体替换为占位符，宁可丢信息也不泄密。
    /// </summary>
    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var result = value;
        foreach (var secret in knownSecrets.Keys.OrderByDescending(static item => item.Length))
        {
            result = result.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        try
        {
            result = JsonSecretRegex().Replace(result, "$1\"<redacted>\"");
            result = TomlSecretRegex().Replace(result, "$1\"<redacted>\"");
            result = AuthorizationRegex().Replace(result, "$1<redacted>");
            result = ApiKeyRegex().Replace(result, "<redacted-api-key>");
            result = QuerySecretRegex().Replace(result, "$1=<redacted>");
        }
        catch (RegexMatchTimeoutException)
        {
            return "<redacted diagnostic: input exceeded redaction budget>";
        }

        return result;
    }

    // HTTP Authorization 头：保留 scheme（含 bearer 前缀），抹除凭据本体
    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?)\\S+", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex AuthorizationRegex();

    // TOML 键值对：识别常见敏感键名，值兼容多行原始串、单双引号串与裸值三种形态
    [GeneratedRegex("(?i)(\\b(?:experimental_bearer_token|api[_-]?key|access[_-]?token|token|password|secret)\\s*=\\s*)(?:\"\"\"[\\s\\S]*?(?:\"\"\"|\\z)|'''[\\s\\S]*?(?:'''|\\z)|\"(?:\\\\.|[^\"\\\\\\r\\n])*(?:\"|$)|'[^'\\r\\n]*(?:'|$)|[^\\s#]+)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex TomlSecretRegex();

    // JSON 键值对：识别常见敏感键名并抹除其字符串值
    [GeneratedRegex("(?i)(\"(?:authorization|experimental_bearer_token|api[_-]?key|access[_-]?token|token|password|secret)\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*(?:\"|$)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex JsonSecretRegex();

    // OpenAI 风格 API Key：sk- 开头的长令牌
    [GeneratedRegex("(?i)\\bsk-[A-Za-z0-9_-]{8,}\\b", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ApiKeyRegex();

    // URL 查询参数：api_key/token/secret 等参数的取值
    [GeneratedRegex("(?i)(api[_-]?key|access[_-]?token|token|secret)=([^&\\s]+)", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex QuerySecretRegex();
}
