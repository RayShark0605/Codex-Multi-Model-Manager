namespace CodexModelManager.Core.Codex;

/// <summary>
/// 推理力度（model_reasoning_effort）取值策略：
/// Provider 侧给出的允许列表统一做“清洗 + 小写化 + 过滤受支持值 + 去重排序”的规范化。
/// </summary>
public static class ReasoningEffortPolicy
{
    /// <summary>把 Provider 声明的允许取值规范化为逗号分隔的小写串（空输入返回空串）。</summary>
    public static string CanonicalizeAllowed(IEnumerable<string>? providerOptions)
    {
        if (providerOptions is null)
        {
            return string.Empty;
        }

        return string.Join(",", providerOptions
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Where(ManagedConfigKeys.SupportedReasoningEfforts.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal));
    }

    /// <summary>把规范化串解析回受支持取值集合（同样过滤与去重；空白输入返回空集合）。</summary>
    public static IReadOnlySet<string> ParseAllowed(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return canonical.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant())
            .Where(ManagedConfigKeys.SupportedReasoningEfforts.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }
}
