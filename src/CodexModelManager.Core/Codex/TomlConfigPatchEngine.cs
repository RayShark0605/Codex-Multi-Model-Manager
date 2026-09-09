using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// Validates with Tomlyn, but patches only exact source spans. It never serializes
/// the user's TOML document, so comments, ordering and unknown configuration survive.
/// </summary>
public sealed class TomlConfigPatchEngine : IConfigPatchEngine
{
    public ConfigPatchResult Apply(string originalText, ConfigPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(request);

        foreach (string key in request.RootValues.Keys)
        {
            if (!ManagedConfigKeys.Root.Contains(key))
            {
                throw new InvalidOperationException($"拒绝修改未登记的 Codex 根配置键: {key}");
            }
        }

        foreach (string table in request.TableBodies.Keys.Concat(request.RemoveTables ?? []))
        {
            if (!ManagedConfigKeys.IsManagedTable(table))
            {
                throw new InvalidOperationException($"拒绝修改未登记的 Codex 配置节: [{table}]");
            }
        }

        TomlSourceDocument parsed = TomlSourceDocument.Parse(originalText);
        string newLine = DetectNewLine(originalText);
        List<TextEdit> edits = [];
        List<ConfigMutation> mutations = [];
        List<string> rootInsertions = [];

        foreach ((string key, string? newRawValue) in request.RootValues)
        {
            TomlSourceAssignment? entry = parsed.Assignments.SingleOrDefault(item => item.IsDocumentRoot && item.Segments.Count == 1 && item.Segments[0] == key);
            if (entry is null)
            {
                if (newRawValue is not null)
                {
                    rootInsertions.Add($"{key} = {newRawValue}");
                    mutations.Add(new ConfigMutation(key, ConfigMutationKind.Add, null, DisplayValue(key, newRawValue), IsSecret(key)));
                }

                continue;
            }

            if (newRawValue is null)
            {
                edits.Add(new TextEdit(entry.Start, entry.Length, string.Empty));
                mutations.Add(new ConfigMutation(key, ConfigMutationKind.Remove, DisplayValue(key, entry.RawValue), null, IsSecret(key)));
                continue;
            }

            if (NormalizeRawValue(entry.RawValue) == NormalizeRawValue(newRawValue))
            {
                continue;
            }

            edits.Add(new TextEdit(entry.ValueStart, entry.ValueLength, newRawValue));
            mutations.Add(new ConfigMutation(key, ConfigMutationKind.Change, DisplayValue(key, entry.RawValue), DisplayValue(key, newRawValue), IsSecret(key)));
        }

        if (rootInsertions.Count > 0)
        {
            int insertAt = parsed.Tables.Count > 0 ? parsed.Tables[0].Start : originalText.Length;
            string prefix = insertAt > 0 && !EndsWithNewLine(originalText.AsSpan(0, insertAt)) ? newLine : string.Empty;
            string suffix = parsed.Tables.Count > 0 ? newLine : (originalText.Length == 0 || EndsWithNewLine(originalText) ? string.Empty : newLine);
            string insertion = prefix + string.Join(newLine, rootInsertions) + newLine + suffix;
            edits.Add(new TextEdit(insertAt, 0, insertion));
        }

        HashSet<string> tablesToRemove = new(StringComparer.Ordinal);
        foreach (string table in request.RemoveTables ?? [])
        {
            tablesToRemove.Add(table);
        }

        foreach (string table in request.TableBodies.Keys)
        {
            tablesToRemove.Add(table);
        }

        IReadOnlyList<string>[] removalSegments = tablesToRemove.Select(TomlDottedKey.ParseSegments).ToArray();
        foreach (TomlSourceTable table in parsed.Tables)
        {
            if (!removalSegments.Any(segments => TomlSourceDocument.IsSameOrDescendant(table.Segments, segments)))
            {
                continue;
            }

            edits.Add(new TextEdit(table.Start, table.Length, string.Empty));
        }

        List<string> tableInsertions = [];
        foreach ((string table, string? body) in request.TableBodies)
        {
            string? oldBody = parsed.Tables.FirstOrDefault(item => item.Path == table)?.Body;
            if (body is null)
            {
                if (oldBody is not null)
                {
                    mutations.Add(new ConfigMutation($"[{table}]", ConfigMutationKind.Remove, "<managed table>", null, ContainsSecret(oldBody)));
                }

                continue;
            }

            string normalizedBody = NormalizeBody(body, newLine);
            tableInsertions.Add($"[{table}]{newLine}{normalizedBody}");
            ConfigMutationKind kind = oldBody is null ? ConfigMutationKind.Add : ConfigMutationKind.Change;
            if (oldBody is null || NormalizeBody(oldBody, "\n") != NormalizeBody(body, "\n"))
            {
                mutations.Add(new ConfigMutation($"[{table}]", kind, oldBody is null ? null : "<managed table>", "<managed table>", ContainsSecret(body)));
            }
        }

        string candidate = ApplyEdits(originalText, edits);
        if (tableInsertions.Count > 0)
        {
            if (candidate.Length > 0 && !EndsWithNewLine(candidate))
            {
                candidate += newLine;
            }

            if (candidate.Length > 0 && !candidate.EndsWith(newLine + newLine, StringComparison.Ordinal))
            {
                candidate += newLine;
            }

            candidate += string.Join(newLine + newLine, tableInsertions);
            if (parsed.HasTrailingNewLine)
            {
                candidate += newLine;
            }
        }

        ConfigReadResult after = Read(candidate);
        return new ConfigPatchResult(
            candidate,
            mutations,
            new PreservationSummary(
                after.McpServerCount,
                after.ProjectCount,
                after.HookSectionCount,
                after.PluginSectionCount,
                true));
    }

    public ConfigReadResult Read(string text)
    {
        TomlSourceDocument parsed = TomlSourceDocument.Parse(text);
        Dictionary<string, string> root = parsed.Assignments.Where(item => item.IsDocumentRoot && item.Segments.Count == 1)
            .GroupBy(item => item.Segments[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single().RawValue, StringComparer.Ordinal);
        Dictionary<string, string> tables = parsed.Tables
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join("\n", group.Select(item => item.Body)), StringComparer.Ordinal);

        return new ConfigReadResult(
            root,
            tables,
            [],
            CountTopLevelTables(parsed.Tables, "mcp_servers"),
            CountTopLevelTables(parsed.Tables, "projects"),
            parsed.Tables.Count(item => item.Path.Equals("hooks", StringComparison.Ordinal) || item.Path.StartsWith("hooks.", StringComparison.Ordinal)),
            parsed.Tables.Count(item => item.Path.Equals("plugins", StringComparison.Ordinal) || item.Path.StartsWith("plugins.", StringComparison.Ordinal)));
    }

    public void Validate(string text) => TomlSourceDocument.ParseSyntax(text);

    private static int CountTopLevelTables(IEnumerable<TomlSourceTable> tables, string root) =>
        tables.Where(item => item.Segments.Count >= 2 && item.Segments[0].Equals(root, StringComparison.Ordinal))
            .Select(item => item.Segments[1])
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static string ApplyEdits(string text, IEnumerable<TextEdit> edits)
    {
        TextEdit[] ordered = edits
            .OrderByDescending(edit => edit.Start)
            .ThenByDescending(edit => edit.Length)
            .ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            TextEdit previous = ordered[i - 1];
            TextEdit current = ordered[i];
            if (current.Start + current.Length > previous.Start)
            {
                throw new InvalidOperationException("内部错误：TOML source spans overlap.");
            }
        }

        StringBuilder builder = new(text);
        foreach (TextEdit edit in ordered)
        {
            builder.Remove(edit.Start, edit.Length);
            builder.Insert(edit.Start, edit.Replacement);
        }

        return builder.ToString();
    }

    private static string NormalizeBody(string body, string newLine)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        return normalized.Replace("\n", newLine, StringComparison.Ordinal);
    }

    private static string NormalizeRawValue(string value) => value.Trim();

    private static string DisplayValue(string key, string rawValue) => IsSecret(key) ? "<redacted>" : rawValue.Trim();

    private static bool IsSecret(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSecret(string value) =>
        value.Contains("experimental_bearer_token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static string DetectNewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static bool EndsWithNewLine(string text) => EndsWithNewLine(text.AsSpan());

    private static bool EndsWithNewLine(ReadOnlySpan<char> text) => text.Length > 0 && text[^1] == '\n';

    private sealed record TextEdit(int Start, int Length, string Replacement);

}
