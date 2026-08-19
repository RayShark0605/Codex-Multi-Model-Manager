using System.Text.RegularExpressions;

namespace CodexModelManager.Core.Infrastructure;

public static partial class SemanticVersion
{
    public static bool IsAtLeast(string? actualText, string requiredText)
    {
        Version? actual = Parse(actualText);
        Version? required = Parse(requiredText);
        return actual is not null && required is not null && actual >= required;
    }

    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        Match match = VersionRegex().Match(value);
        return match.Success && Version.TryParse(match.Value, out Version? version) ? version : null;
    }

    [GeneratedRegex("\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
