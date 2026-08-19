namespace CodexModelManager.Core.Codex;

public static class ManagedConfigKeys
{
    public static IReadOnlySet<string> SupportedReasoningEfforts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        // Current Codex Desktop model catalogs expose max even though the public
        // reference's compact enum table can lag model-specific capabilities.
        "max",
    };

    public static IReadOnlySet<string> Root { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "model_provider",
        "model_catalog_json",
        "model_context_window",
        "model_auto_compact_token_limit",
        "model_auto_compact_token_limit_scope",
        "model_reasoning_effort",
        "preferred_auth_method",
        "forced_login_method",
        "openai_base_url",
    };

    public static IReadOnlySet<string> ProviderTables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "model_providers.deepseek",
        "model_providers.lmstudio_local_cmm",
    };

    public static bool IsManagedTable(string tablePath) =>
        ProviderTables.Any(path =>
            tablePath.Equals(path, StringComparison.Ordinal) ||
            tablePath.StartsWith(path + ".", StringComparison.Ordinal));
}
