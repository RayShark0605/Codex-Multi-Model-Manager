namespace CodexModelManager.Core.Codex;

/// <summary>
/// LM Studio 流韧性键：写入 Codex Provider 表的 SSE/请求重试参数。
/// 本地大模型在长 prompt（如自动压缩）下可能长时间不产生 SSE 事件，超过 Codex 默认
/// stream_idle_timeout_ms = 300000 时会被判定为流失活并重发 sampling request；
/// 这些键只能写在 model_providers 表内（官方 Configuration Reference），而内置
/// lmstudio ID 无法被 config.toml 覆盖（源码对非 Bedrock 内置 ID 一律 or_insert），
/// 因此 LM Studio 一律使用 lmstudio_local_cmm 自建表承载这些键。
/// </summary>
public static class LmStudioStreamResilience
{
    /// <summary>空闲超时（毫秒）：约 33 分钟，覆盖本地大模型长 prefill；默认 300000。</summary>
    public const int StreamIdleTimeoutMs = 2_000_000;

    /// <summary>SSE 流最大重试次数：低于默认 5，减少断流后重发采样请求。</summary>
    public const int StreamMaxRetries = 2;

    /// <summary>HTTP 请求最大重试次数：低于默认 4，与流重试策略保持一致。</summary>
    public const int RequestMaxRetries = 2;

    /// <summary>追加到 Provider 表体的三行流韧性键（不含结尾换行）；数值与上方常量保持一致。</summary>
    public const string TableBody = "stream_idle_timeout_ms = 2000000\nstream_max_retries = 2\nrequest_max_retries = 2";
}
