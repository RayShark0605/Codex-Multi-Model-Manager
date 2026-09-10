using System.Text.RegularExpressions;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 语义化版本（SemVer）解析与比较工具。
/// 支持最多 4 段的核心版本号、预发布标识与构建元数据（比较时忽略元数据）。
/// </summary>
public static partial class SemanticVersion
{
    /// <summary>
    /// 判断 <paramref name="actualText"/> 表示的版本是否不低于 <paramref name="requiredText"/>。
    /// 任一侧无法解析为有效版本时返回 false。
    /// </summary>
    public static bool IsAtLeast(string? actualText, string requiredText)
    {
        ParsedSemanticVersion? actual = ParseSemantic(actualText);
        ParsedSemanticVersion? required = ParseSemantic(requiredText);
        return actual is not null && required is not null && Compare(actual, required) >= 0;
    }

    /// <summary>
    /// 将版本文本解析为 BCL 的 <see cref="Version"/>（固定 4 段）；空白或无法解析时返回 null。
    /// </summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        ParsedSemanticVersion? parsed = ParseSemantic(value);
        return parsed is null ? null : new Version(parsed.Core[0], parsed.Core[1], parsed.Core[2], parsed.Core[3]);
    }

    /// <summary>
    /// 用正则从文本中提取语义化版本；核心段逐位解析为 int，预发布段按“.”切分（无则为空数组）。
    /// </summary>
    private static ParsedSemanticVersion? ParseSemantic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = VersionRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        string[] rawCore = match.Groups["core"].Value.Split('.');
        int[] core = new int[4];
        for (int index = 0; index < rawCore.Length; index++)
        {
            if (!int.TryParse(rawCore[index], out core[index]))
            {
                return null;
            }
        }

        // 预发布组形如 “-alpha.1”，去掉前导 “-” 后按 “.” 切分
        string[] prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value[1..].Split('.') : [];
        return new ParsedSemanticVersion(core, prerelease);
    }

    /// <summary>
    /// 按 SemVer 规则比较两个已解析版本：先比核心段，再比预发布标识。
    /// </summary>
    private static int Compare(ParsedSemanticVersion left, ParsedSemanticVersion right)
    {
        for (int index = 0; index < left.Core.Length; index++)
        {
            int comparison = left.Core[index].CompareTo(right.Core[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        // SemVer 规定：无预发布标识的版本高于有预发布标识的版本
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
        {
            return left.Prerelease.Length == right.Prerelease.Length ? 0 : left.Prerelease.Length == 0 ? 1 : -1;
        }

        // 逐个比较公共长度的预发布标识，全部相等则标识数多者版本更高
        int sharedLength = Math.Min(left.Prerelease.Length, right.Prerelease.Length);
        for (int index = 0; index < sharedLength; index++)
        {
            int comparison = ComparePrereleaseIdentifier(left.Prerelease[index], right.Prerelease[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    /// <summary>
    /// 比较单个预发布标识：纯数字按数值比较（先比位数再比字典序），
    /// 数字标识低于字母标识，同为字母时按序数字典序比较。
    /// </summary>
    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        bool leftNumeric = left.All(char.IsDigit);
        bool rightNumeric = right.All(char.IsDigit);
        if (leftNumeric && rightNumeric)
        {
            // 去前导零后，位数多者数值更大；位数相同按字典序即可
            string normalizedLeft = left.TrimStart('0');
            string normalizedRight = right.TrimStart('0');
            normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
            normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
            int lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            return lengthComparison != 0 ? lengthComparison : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    /// <summary>语义化版本解析结果：核心段固定 4 位 int，预发布段为标识数组。</summary>
    private sealed record ParsedSemanticVersion(int[] Core, string[] Prerelease);

    // 匹配 core（2~4 段数字）、可选 pre（“-”开头）与可选构建元数据（“+”开头，比较时忽略）
    [GeneratedRegex("(?<!\\d)(?<core>\\d+\\.\\d+(?:\\.\\d+){0,2})(?<pre>-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?![0-9A-Za-z.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
