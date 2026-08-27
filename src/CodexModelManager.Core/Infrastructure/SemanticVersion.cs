using System.Text.RegularExpressions;

namespace CodexModelManager.Core.Infrastructure;

public static partial class SemanticVersion
{
    public static bool IsAtLeast(string? actualText, string requiredText)
    {
        ParsedSemanticVersion? actual = ParseSemantic(actualText);
        ParsedSemanticVersion? required = ParseSemantic(requiredText);
        return actual is not null && required is not null && Compare(actual, required) >= 0;
    }

    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        ParsedSemanticVersion? parsed = ParseSemantic(value);
        return parsed is null
            ? null
            : new Version(parsed.Core[0], parsed.Core[1], parsed.Core[2], parsed.Core[3]);
    }

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

        string[] prerelease = match.Groups["pre"].Success
            ? match.Groups["pre"].Value[1..].Split('.')
            : [];
        return new ParsedSemanticVersion(core, prerelease);
    }

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

        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
        {
            return left.Prerelease.Length == right.Prerelease.Length
                ? 0
                : left.Prerelease.Length == 0 ? 1 : -1;
        }

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

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        bool leftNumeric = left.All(char.IsDigit);
        bool rightNumeric = right.All(char.IsDigit);
        if (leftNumeric && rightNumeric)
        {
            string normalizedLeft = left.TrimStart('0');
            string normalizedRight = right.TrimStart('0');
            normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
            normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
            int lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private sealed record ParsedSemanticVersion(int[] Core, string[] Prerelease);

    [GeneratedRegex("(?<!\\d)(?<core>\\d+\\.\\d+(?:\\.\\d+){0,2})(?<pre>-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?![0-9A-Za-z.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
