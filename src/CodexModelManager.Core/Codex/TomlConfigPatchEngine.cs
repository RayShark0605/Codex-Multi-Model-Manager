using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;
using Tomlyn.Parsing;

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
        Validate(originalText);

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

        ParsedToml parsed = ParsedToml.Parse(originalText);
        string newLine = DetectNewLine(originalText);
        List<TextEdit> edits = [];
        List<ConfigMutation> mutations = [];
        List<string> rootInsertions = [];

        foreach ((string key, string? newRawValue) in request.RootValues)
        {
            RootEntry? entry = parsed.RootEntries.SingleOrDefault(item => item.Key == key);
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

            string replacement = $"{entry.Indent}{entry.KeyText}{entry.BeforeEquals}={entry.AfterEquals}{newRawValue}{entry.InlineComment}{entry.LineEnding}";
            edits.Add(new TextEdit(entry.Start, entry.Length, replacement));
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

        foreach (TableEntry table in parsed.Tables)
        {
            if (!tablesToRemove.Any(path => IsTableOrDescendant(table.Path, path)))
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

        Validate(candidate);
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
        Validate(text);
        ParsedToml parsed = ParsedToml.Parse(text);
        Dictionary<string, string> root = parsed.RootEntries
            .GroupBy(item => item.Key, StringComparer.Ordinal)
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

    public void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Tomlyn.Syntax.DocumentSyntax document;
        try
        {
            document = SyntaxParser.ParseStrict(text, "config.toml", true);
        }
        catch (Tomlyn.TomlException exception)
        {
            throw new InvalidDataException("config.toml 语法或语义无效:" + Environment.NewLine + exception.Message, exception);
        }

        if (document.HasErrors)
        {
            string diagnostics = string.Join(Environment.NewLine, document.Diagnostics.Select(item => item.ToString()));
            throw new InvalidDataException("config.toml 语法或语义无效:" + Environment.NewLine + diagnostics);
        }
    }

    private static int CountTopLevelTables(IEnumerable<TableEntry> tables, string root) =>
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

    private static bool IsTableOrDescendant(string candidate, string parent) =>
        candidate.Equals(parent, StringComparison.Ordinal) || candidate.StartsWith(parent + ".", StringComparison.Ordinal);

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

    private sealed record RootEntry(
        string Key,
        string KeyText,
        string RawValue,
        string Indent,
        string BeforeEquals,
        string AfterEquals,
        string InlineComment,
        string LineEnding,
        int Start,
        int Length);

    private sealed record TableEntry(string Path, IReadOnlyList<string> Segments, string Body, int Start, int Length);

    private sealed class ParsedToml
    {
        private ParsedToml(List<RootEntry> rootEntries, List<TableEntry> tables, bool hasTrailingNewLine)
        {
            RootEntries = rootEntries;
            Tables = tables;
            HasTrailingNewLine = hasTrailingNewLine;
        }

        public List<RootEntry> RootEntries { get; }

        public List<TableEntry> Tables { get; }

        public bool HasTrailingNewLine { get; }

        public static ParsedToml Parse(string text)
        {
            List<LineInfo> lines = SplitLines(text);
            List<(int Index, string Path, IReadOnlyList<string> Segments)> headers = [];
            bool inMultilineBasic = false;
            bool inMultilineLiteral = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string content = lines[i].Content;
                if (!inMultilineBasic && !inMultilineLiteral)
                {
                    string trimmed = content.TrimStart();
                    if (TryParseHeader(trimmed, out string? path, out IReadOnlyList<string>? segments))
                    {
                        headers.Add((i, path!, segments!));
                    }
                }

                UpdateMultilineState(content, ref inMultilineBasic, ref inMultilineLiteral);
            }

            int firstHeaderLine = headers.Count == 0 ? lines.Count : headers[0].Index;
            List<RootEntry> roots = [];
            inMultilineBasic = false;
            inMultilineLiteral = false;
            for (int i = 0; i < firstHeaderLine; i++)
            {
                LineInfo line = lines[i];
                if (!inMultilineBasic && !inMultilineLiteral && TryParseRoot(line, out RootEntry? root))
                {
                    roots.Add(root!);
                }

                UpdateMultilineState(line.Content, ref inMultilineBasic, ref inMultilineLiteral);
            }

            List<TableEntry> tables = [];
            for (int i = 0; i < headers.Count; i++)
            {
                (int lineIndex, string path, IReadOnlyList<string> segments) = headers[i];
                int start = lines[lineIndex].Start;
                int end = i + 1 < headers.Count ? lines[headers[i + 1].Index].Start : text.Length;
                int bodyStart = lines[lineIndex].Start + lines[lineIndex].FullLength;
                string body = bodyStart <= end ? text[bodyStart..end] : string.Empty;
                tables.Add(new TableEntry(path, segments, body, start, end - start));
            }

            return new ParsedToml(roots, tables, text.EndsWith('\n'));
        }

        private static bool TryParseHeader(string line, out string? path, out IReadOnlyList<string>? segments)
        {
            path = null;
            segments = null;
            if (line.Length == 0 || line[0] != '[')
            {
                return false;
            }

            bool arrayTable = line.StartsWith("[[", StringComparison.Ordinal);
            int contentStart = arrayTable ? 2 : 1;
            int close = FindHeaderClose(line, contentStart, arrayTable);
            if (close <= contentStart)
            {
                return false;
            }

            int closingLength = arrayTable ? 2 : 1;
            string tail = line[(close + closingLength)..].TrimStart();
            if (tail.Length > 0 && tail[0] != '#')
            {
                return false;
            }

            segments = ParseDottedKeySegments(line[contentStart..close]);
            path = string.Join('.', segments);
            return true;
        }

        private static int FindHeaderClose(string text, int start, bool arrayTable)
        {
            bool basic = false;
            bool literal = false;
            bool escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char character = text[i];
                if (basic)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') basic = false;
                    continue;
                }

                if (literal)
                {
                    if (character == '\'') literal = false;
                    continue;
                }

                if (character == '"') basic = true;
                else if (character == '\'') literal = true;
                else if (character == ']' && (!arrayTable || (i + 1 < text.Length && text[i + 1] == ']'))) return i;
            }

            return -1;
        }

        private static bool TryParseRoot(LineInfo line, out RootEntry? entry)
        {
            entry = null;
            int equals = FindUnquoted(line.Content, '=');
            if (equals <= 0)
            {
                return false;
            }

            string left = line.Content[..equals];
            string keyText = left.Trim();
            if (keyText.Length == 0 || keyText.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-')))
            {
                return false;
            }

            int valueStart = equals + 1;
            while (valueStart < line.Content.Length && char.IsWhiteSpace(line.Content[valueStart]))
            {
                valueStart++;
            }

            int comment = FindComment(line.Content, valueStart);
            int valueEnd = comment < 0 ? line.Content.Length : comment;
            while (valueEnd > valueStart && char.IsWhiteSpace(line.Content[valueEnd - 1]))
            {
                valueEnd--;
            }

            string indent = left[..(left.Length - left.TrimStart().Length)];
            int keyStart = indent.Length;
            int keyEnd = keyStart + keyText.Length;
            string beforeEquals = left[keyEnd..];
            string afterEquals = line.Content[(equals + 1)..valueStart];
            string inlineComment = line.Content[valueEnd..];
            string rawValue = line.Content[valueStart..valueEnd];
            entry = new RootEntry(
                keyText,
                keyText,
                rawValue,
                indent,
                beforeEquals,
                afterEquals,
                inlineComment,
                line.LineEnding,
                line.Start,
                line.FullLength);
            return true;
        }

        private static int FindComment(string text, int start)
        {
            bool basic = false;
            bool literal = false;
            bool escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char character = text[i];
                if (basic)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        basic = false;
                    }

                    continue;
                }

                if (literal)
                {
                    if (character == '\'')
                    {
                        literal = false;
                    }

                    continue;
                }

                if (character == '"') basic = true;
                else if (character == '\'') literal = true;
                else if (character == '#') return i;
            }

            return -1;
        }

        private static int FindUnquoted(string text, char target)
        {
            bool basic = false;
            bool literal = false;
            bool escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (basic)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') basic = false;
                    continue;
                }

                if (literal)
                {
                    if (character == '\'') literal = false;
                    continue;
                }

                if (character == '"') basic = true;
                else if (character == '\'') literal = true;
                else if (character == target) return i;
            }

            return -1;
        }

        private static List<string> ParseDottedKeySegments(string key)
        {
            List<string> parts = [];
            var current = new StringBuilder();
            bool basic = false;
            bool literal = false;
            bool escaped = false;
            foreach (char character in key)
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
                    parts.Add(NormalizeKeySegment(current.ToString()));
                    current.Clear();
                }
                else
                {
                    current.Append(character);
                }
            }

            parts.Add(NormalizeKeySegment(current.ToString()));
            return parts;
        }

        private static string NormalizeKeySegment(string value)
        {
            string segment = value.Trim();
            if (segment.Length >= 2 && segment[0] == '\'' && segment[^1] == '\'') return segment[1..^1];
            if (segment.Length >= 2 && segment[0] == '"' && segment[^1] == '"')
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<string>(segment) ?? segment[1..^1]; }
                catch (System.Text.Json.JsonException) { return segment[1..^1]; }
            }

            return segment;
        }

        private static void UpdateMultilineState(string line, ref bool basic, ref bool literal)
        {
            int basicCount = CountUnescaped(line, "\"\"\"");
            int literalCount = CountUnescaped(line, "'''");
            if (!literal && basicCount % 2 == 1) basic = !basic;
            if (!basic && literalCount % 2 == 1) literal = !literal;
        }

        private static int CountUnescaped(string text, string token)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                if (index == 0 || text[index - 1] != '\\') count++;
                index += token.Length;
            }

            return count;
        }

        private static List<LineInfo> SplitLines(string text)
        {
            List<LineInfo> lines = [];
            int start = 0;
            while (start < text.Length)
            {
                int newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    lines.Add(new LineInfo(start, text[start..], string.Empty));
                    break;
                }

                int contentEnd = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
                string ending = contentEnd < newline ? "\r\n" : "\n";
                lines.Add(new LineInfo(start, text[start..contentEnd], ending));
                start = newline + 1;
            }

            if (text.Length == 0 || text.EndsWith('\n'))
            {
                lines.Add(new LineInfo(text.Length, string.Empty, string.Empty));
            }

            return lines;
        }

        private sealed record LineInfo(int Start, string Content, string LineEnding)
        {
            public int FullLength => Content.Length + LineEnding.Length;
        }
    }
}
