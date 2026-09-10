using CodexModelManager.Core.Abstractions;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// Codex CLI 主目录（CODEX_HOME）解析器。
/// 优先级：构造参数覆盖 &gt; CODEX_HOME 环境变量 &gt; 用户目录下的 ~/.codex。
/// </summary>
public sealed class DefaultCodexHomeProvider(string? overridePath = null) : ICodexHomeProvider
{
    /// <summary>按优先级解析并返回 Codex 主目录的绝对路径（覆盖值支持环境变量展开）。</summary>
    public string GetCodexHome()
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        }

        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }
}
