using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

public sealed record SecondaryOverrideReplacement(string Value, string? RawTomlValue = null);

public static class SecondaryOverridePatcher
{
    public static (string Text, IReadOnlyList<ConfigMutation> Mutations) Apply(
        string text,
        IReadOnlyDictionary<string, string> replacements) =>
        Apply(text, replacements.ToDictionary(pair => pair.Key, pair => new SecondaryOverrideReplacement(pair.Value), StringComparer.Ordinal));

    public static (string Text, IReadOnlyList<ConfigMutation> Mutations) Apply(
        string text,
        IReadOnlyDictionary<string, SecondaryOverrideReplacement> replacements)
    {
        if (replacements.Count == 0) return (text, []);
        TomlSourceDocument document = TomlSourceDocument.Parse(text);
        List<ConfigMutation> mutations = [];
        List<(int Start, int Length, string Value)> edits = [];
        foreach (IGrouping<string, TomlSourceAssignment> group in document.Assignments.GroupBy(item => item.Path, StringComparer.Ordinal))
        {
            if (!replacements.TryGetValue(group.Key, out SecondaryOverrideReplacement? replacement)) continue;
            TomlSourceAssignment assignment = group.First();
            if (group.Count() != 1 || assignment.IsArrayMember)
            {
                throw new InvalidDataException("Secondary Override 选择无法唯一定位 array-table 项，禁止自动修改。");
            }

            if (assignment.StringValue is not string old) continue;
            if (old == replacement.Value && replacement.RawTomlValue is null) continue;
            string encoded = replacement.RawTomlValue ?? JsonSerializer.Serialize(replacement.Value);
            TomlSourceDocument replacementDocument = TomlSourceDocument.Parse("value = " + encoded);
            if (replacementDocument.Tables.Count != 0 || replacementDocument.Assignments.Count != 1 ||
                replacementDocument.Assignments[0].RawValue != encoded || replacementDocument.Assignments[0].StringValue != replacement.Value)
            {
                throw new InvalidDataException("Secondary Override 的原始 TOML 字符串无效或与记录语义不一致。");
            }

            if (assignment.RawValue == encoded) continue;
            edits.Add((assignment.ValueStart, assignment.ValueLength, encoded));
            mutations.Add(new ConfigMutation(assignment.Path, ConfigMutationKind.Change, old, replacement.Value));
        }

        var builder = new StringBuilder(text);
        foreach ((int start, int length, string value) in edits.OrderByDescending(edit => edit.Start))
        {
            builder.Remove(start, length).Insert(start, value);
        }

        string candidate = builder.ToString();
        TomlSourceDocument.ParseSyntax(candidate);
        return (candidate, mutations);
    }
}
