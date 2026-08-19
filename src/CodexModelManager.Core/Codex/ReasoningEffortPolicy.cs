namespace CodexModelManager.Core.Codex;

public static class ReasoningEffortPolicy
{
    public static string CanonicalizeAllowed(IEnumerable<string>? providerOptions)
    {
        if (providerOptions is null)
        {
            return string.Empty;
        }

        return string.Join(",", providerOptions
            .Where(ManagedConfigKeys.SupportedReasoningEfforts.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal));
    }

    public static IReadOnlySet<string> ParseAllowed(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return canonical.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ManagedConfigKeys.SupportedReasoningEfforts.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }
}
