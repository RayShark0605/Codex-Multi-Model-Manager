using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CodexModelManager.Core.Security;

public sealed partial class SecretRedactor
{
    private readonly ConcurrentDictionary<string, byte> knownSecrets = new(StringComparer.Ordinal);

    public void Register(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4)
        {
            knownSecrets.TryAdd(secret, 0);
        }
    }

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

        result = AuthorizationRegex().Replace(result, "$1<redacted>");
        result = TomlSecretRegex().Replace(result, "$1\"<redacted>\"");
        result = ApiKeyRegex().Replace(result, "<redacted-api-key>");
        result = QuerySecretRegex().Replace(result, "$1=<redacted>");
        return result;
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?)\\S+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex("(?i)(\\b(?:experimental_bearer_token|api[_-]?key|token|password)\\s*=\\s*)[\"'].*?[\"']")]
    private static partial Regex TomlSecretRegex();

    [GeneratedRegex("(?i)\\bsk-[A-Za-z0-9_-]{8,}\\b")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex("(?i)(api[_-]?key|access[_-]?token|token|secret)=([^&\\s]+)")]
    private static partial Regex QuerySecretRegex();
}
