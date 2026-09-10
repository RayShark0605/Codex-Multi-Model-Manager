namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// LM Studio「每模型默认值」持久化能力的版本兼容判定：
/// 采用最低版本下限而非写死版本族——0.4.21 起 per-model defaults 格式存在，
/// 更高版本（含未来升级）一律放行，实际格式是否兼容由 defaults 文件的严格结构校验兜底，
/// 结构不符时会在任何写入发生前安全阻断。
/// </summary>
internal static class LmStudioPerModelDefaultsCompatibility
{
    /// <summary>受支持的最低版本（per-model defaults 格式引入版本）。</summary>
    internal const string MinimumSupportedVersion = "0.4.21";

    /// <summary>受支持范围描述（用于界面与诊断文案）。</summary>
    internal const string SupportedVersionFamilies = MinimumSupportedVersion + " 及以上";

    private static readonly Version MinimumVersion = new(0, 4, 21);

    /// <summary>判断版本文本是否达到受支持下限；空白、预发布、多次“+”或非法元数据均不支持。</summary>
    internal static bool IsSupportedVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        string numericVersion = versionText.Trim();
        int metadataSeparator = numericVersion.IndexOf('+');
        if (metadataSeparator >= 0)
        {
            // 构建元数据只允许一段纯数字（如 0.4.23+7），其余形态一律不支持
            string buildMetadata = numericVersion[(metadataSeparator + 1)..];
            if (buildMetadata.Length == 0 || buildMetadata.Any(character => !char.IsAsciiDigit(character)) || numericVersion.IndexOf('+', metadataSeparator + 1) >= 0)
            {
                return false;
            }

            numericVersion = numericVersion[..metadataSeparator];
        }

        if (numericVersion.Contains('-') || !Version.TryParse(numericVersion, out Version? version))
        {
            return false;
        }

        return version >= MinimumVersion;
    }
}
