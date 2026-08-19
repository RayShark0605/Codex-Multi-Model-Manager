using CodexModelManager.Core.Abstractions;

namespace CodexModelManager.Core.Infrastructure;

public sealed class DefaultCodexHomeProvider(string? overridePath = null) : ICodexHomeProvider
{
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
