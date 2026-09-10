namespace CodexModelManager.Core.Security;

/// <summary>各 Provider 的凭据目标名快捷常量（实际值统一由 <see cref="CredentialTargets"/> 派生）。</summary>
public static class CredentialNames
{
    /// <summary>DeepSeek 凭据目标名。</summary>
    public const string DeepSeek = CredentialTargets.DeepSeek;

    /// <summary>LM Studio 凭据目标名。</summary>
    public const string LmStudio = CredentialTargets.LmStudio;

    /// <summary>GLM 凭据目标名。</summary>
    public const string Glm = CredentialTargets.Glm;
}
