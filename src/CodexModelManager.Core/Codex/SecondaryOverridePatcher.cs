using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>一条 Secondary Override 替换：新值与可选的原始 TOML 值（保留原文形态）。</summary>
public sealed record SecondaryOverrideReplacement(string Value, string? RawTomlValue = null);

/// <summary>
/// Secondary Override 补丁器：按“文件|键路径”到替换值的映射改写 TOML 文本。
/// 只改值跨距，从后往前应用编辑；替换后重新解析校验，保证产出仍是合法 TOML。
/// </summary>
public static class SecondaryOverridePatcher
{
    /// <summary>应用纯文本替换（无原始 TOML 值）的重载。</summary>
    public static (string Text, IReadOnlyList<ConfigMutation> Mutations) Apply(string text, IReadOnlyDictionary<string, string> replacements) =>
        Apply(text, replacements.ToDictionary(pair => pair.Key, pair => new SecondaryOverrideReplacement(pair.Value), StringComparer.Ordinal));

    /// <summary>
    /// 应用替换：逐路径定位赋值，要求可唯一定位且为字符串值；新值（或原始 TOML 值）
    /// 会先在独立文档中验证可解析且语义一致，全部编辑应用后再整体校验候选文本。
    /// </summary>
    public static (string Text, IReadOnlyList<ConfigMutation> Mutations) Apply(string text, IReadOnlyDictionary<string, SecondaryOverrideReplacement> replacements)
    {
        if (replacements.Count == 0)
        {
            return (text, []);
        }

        TomlSourceDocument document = TomlSourceDocument.Parse(text);
        List<ConfigMutation> mutations = [];
        List<(int Start, int Length, string Value)> edits = [];
        foreach (IGrouping<string, TomlSourceAssignment> group in document.Assignments.GroupBy(item => item.Path, StringComparer.Ordinal))
        {
            if (!replacements.TryGetValue(group.Key, out SecondaryOverrideReplacement? replacement))
            {
                continue;
            }

            TomlSourceAssignment assignment = group.First();
            if (group.Count() != 1 || assignment.IsArrayMember)
            {
                throw new InvalidDataException("Secondary Override 选择无法唯一定位 array-table 项，禁止自动修改。");
            }

            if (assignment.StringValue is not string old)
            {
                continue;
            }

            // 值与可选原始形态都未变化则跳过
            if (old == replacement.Value && replacement.RawTomlValue is null)
            {
                continue;
            }

            string encoded = replacement.RawTomlValue ?? JsonSerializer.Serialize(replacement.Value);
            TomlSourceDocument replacementDocument = TomlSourceDocument.Parse("value = " + encoded);
            if (replacementDocument.Tables.Count != 0 || replacementDocument.Assignments.Count != 1 || replacementDocument.Assignments[0].RawValue != encoded || replacementDocument.Assignments[0].StringValue != replacement.Value)
            {
                throw new InvalidDataException("Secondary Override 的原始 TOML 字符串无效或与记录语义不一致。");
            }

            if (assignment.RawValue == encoded)
            {
                continue;
            }

            edits.Add((assignment.ValueStart, assignment.ValueLength, encoded));
            mutations.Add(new ConfigMutation(assignment.Path, ConfigMutationKind.Change, old, replacement.Value));
        }

        // 从后往前应用编辑，避免前面的改动使后续跨距失效
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
