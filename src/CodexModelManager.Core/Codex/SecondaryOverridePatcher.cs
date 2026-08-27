using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

public sealed record SecondaryOverrideReplacement(string Value, string? RawTomlValue = null);

public static partial class SecondaryOverridePatcher
{
    public static (string Text, IReadOnlyList<ConfigMutation> Mutations) Apply(
        string text,
        IReadOnlyDictionary<string, string> replacements) =>
        Apply(text, replacements.ToDictionary(
            pair => pair.Key,
            pair => new SecondaryOverrideReplacement(pair.Value),
            StringComparer.Ordinal));

    public static (string Text, IReadOnlyList<ConfigMutation> Mutations) Apply(
        string text,
        IReadOnlyDictionary<string, SecondaryOverrideReplacement> replacements)
    {
        if (replacements.Count == 0) return (text, []);
        IReadOnlyList<string> currentTableSegments = [];
        List<Line> lines = SplitLines(text);
        List<ConfigMutation> mutations = [];
        var lexicalState = new TomlLineLexicalState();
        for (int index = 0; index < lines.Count; index++)
        {
            Line line = lines[index];
            if (!lexicalState.IsCodeLineAndAdvance(line.Content))
            {
                continue;
            }

            Match header = TableHeaderRegex().Match(line.Content);
            if (header.Success)
            {
                currentTableSegments = TomlDottedKey.ParseSegments(header.Groups["table"].Success ? header.Groups["table"].Value : header.Groups["array"].Value);
                continue;
            }

            Match assignment = AssignmentRegex().Match(line.Content);
            if (!assignment.Success) continue;
            IReadOnlyList<string> keySegments = TomlDottedKey.ParseSegments(assignment.Groups["key"].Value);
            string keyPath = TomlDottedKey.Canonical([.. currentTableSegments, .. keySegments]);
            if (!replacements.TryGetValue(keyPath, out SecondaryOverrideReplacement? replacement)) continue;
            string old = ReadString(assignment);
            if (old == replacement.Value && replacement.RawTomlValue is null) continue;
            string encoded = replacement.RawTomlValue ?? JsonSerializer.Serialize(replacement.Value);
            if (replacement.RawTomlValue is not null && !QuotedStringRegex().IsMatch(replacement.RawTomlValue))
            {
                throw new InvalidDataException($"Secondary Override 的原始 TOML 字符串无效: {keyPath}");
            }

            if (assignment.Groups["quoted"].Value == encoded) continue;
            string prefix = line.Content[..assignment.Groups["quoted"].Index];
            string suffix = line.Content[(assignment.Groups["quoted"].Index + assignment.Groups["quoted"].Length)..];
            lines[index] = line with { Content = prefix + encoded + suffix };
            mutations.Add(new ConfigMutation(keyPath, ConfigMutationKind.Change, old, replacement.Value));
        }

        var builder = new StringBuilder(text.Length + 64);
        foreach (Line line in lines) builder.Append(line.Content).Append(line.Ending);
        return (builder.ToString(), mutations);
    }

    private static List<Line> SplitLines(string text)
    {
        List<Line> lines = [];
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(new Line(text[start..], string.Empty));
                return lines;
            }

            int end = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(new Line(text[start..end], end < newline ? "\r\n" : "\n"));
            start = newline + 1;
        }

        return lines;
    }

    private static string ReadString(Match assignment)
    {
        if (assignment.Groups["literal"].Success) return assignment.Groups["literal"].Value;
        string value = assignment.Groups["basic"].Value;
        try { return JsonSerializer.Deserialize<string>('"' + value + '"') ?? value; }
        catch (JsonException) { return value; }
    }

    private sealed record Line(string Content, string Ending);

    [GeneratedRegex("^\\s*(?:\\[\\[(?<array>[^]]+)\\]\\]|\\[(?<table>[^]]+)\\])\\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableHeaderRegex();

    [GeneratedRegex("""^\s*(?<key>(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*')(?:\s*\.\s*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*'))*)\s*=\s*(?<quoted>"(?<basic>(?:\\.|[^"\\])*)"|'(?<literal>[^']*)')""", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex("""^(?:"(?:\\.|[^"\\])*"|'[^']*')$""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedStringRegex();
}
