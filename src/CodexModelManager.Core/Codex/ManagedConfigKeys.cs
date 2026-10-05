namespace CodexModelManager.Core.Codex;

/// <summary>
/// 本管理器“纳管”的 Codex 配置键集合：根键、Provider 表与推理力度枚举。
/// 切换/纳管逻辑只允许改写这些键，其余内容一律原样保留。
/// </summary>
public static class ManagedConfigKeys
{
    /// <summary>受支持的 model_reasoning_effort 取值（严格区分大小写）。</summary>
    public static IReadOnlySet<string> SupportedReasoningEfforts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        // 当前 Codex Desktop 的模型目录已暴露 max，公共参考文档的精简枚举表可能滞后于具体模型能力
        "max",
    };

    /// <summary>纳管的根级键。</summary>
    public static IReadOnlySet<string> Root { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "model_provider",
        "model_catalog_json",
        "model_context_window",
        "model_auto_compact_token_limit",
        "model_auto_compact_token_limit_scope",
        "tool_output_token_limit",
        "model_reasoning_effort",
        "preferred_auth_method",
        "forced_login_method",
        "openai_base_url",
        // LM Studio 本地模型的思考只经 response.reasoning_text.delta（raw reasoning）流式输出，
        // Codex 渲染该通道的唯一门控即本键（默认 false）；切离 LM Studio 时按 Provider 快照恢复原值
        "show_raw_agent_reasoning",
    };

    /// <summary>纳管的 Provider 表（含其子表路径）。</summary>
    public static IReadOnlySet<string> ProviderTables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "model_providers.deepseek",
        "model_providers.lmstudio_local_cmm",
        "model_providers.ZAI",
    };

    /// <summary>判断给定表路径是否属于纳管的 Provider 表或其子表。</summary>
    public static bool IsManagedTable(string tablePath) => ProviderTables.Any(path => tablePath.Equals(path, StringComparison.Ordinal) || tablePath.StartsWith(path + ".", StringComparison.Ordinal));
}
