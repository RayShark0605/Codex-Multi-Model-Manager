using System.Text;
using System.Text.Json;

namespace CodexModelManager.Core.Codex;

internal static class TomlDottedKey
{
    public static IReadOnlyList<string> ParseSegments(string value)
    {
        List<string> segments = [];
        var current = new StringBuilder();
        bool basic = false;
        bool literal = false;
        bool escaped = false;
        foreach (char character in value)
        {
            if (basic)
            {
                current.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') basic = false;
                continue;
            }

            if (literal)
            {
                current.Append(character);
                if (character == '\'') literal = false;
                continue;
            }

            if (character == '"')
            {
                basic = true;
                current.Append(character);
            }
            else if (character == '\'')
            {
                literal = true;
                current.Append(character);
            }
            else if (character == '.')
            {
                segments.Add(DecodeSegment(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        segments.Add(DecodeSegment(current.ToString()));
        return segments;
    }

    public static string Canonical(string value) => Canonical(ParseSegments(value));

    public static string Canonical(IEnumerable<string> segments) => string.Join('.', segments.Select(EncodeSegment));

    private static string DecodeSegment(string value)
    {
        string segment = value.Trim();
        if (segment.Length >= 2 && segment[0] == '\'' && segment[^1] == '\'') return segment[1..^1];
        if (segment.Length >= 2 && segment[0] == '"' && segment[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(segment) ?? segment[1..^1];
            }
            catch (JsonException)
            {
                return segment[1..^1];
            }
        }

        return segment;
    }

    private static string EncodeSegment(string value) =>
        value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? value
            : JsonSerializer.Serialize(value);
}
