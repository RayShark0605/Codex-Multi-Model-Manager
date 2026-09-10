using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// TOML 配置补丁引擎：用 Tomlyn 做语法校验，但编辑只针对原文跨距（span）——
/// 从不序列化用户的 TOML 文档，因此注释、顺序与未知配置都能原样保留。
/// </summary>
public sealed class TomlConfigPatchEngine : IConfigPatchEngine
{
    /// <summary>
    /// 应用补丁：先校验只动纳管键/表，再按“根键改值或插入、表整体删除或追加”生成编辑，
    /// 从后往前应用后重新读取，返回新文本与变更/保留摘要。
    /// </summary>
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
                // 文档根没有该键：新增（newRawValue 为 null 表示无需处理）
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

        // 根键插入点：首个表之前（无表则文末），保证根键始终位于任何表头之前
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
        // 表插入统一追加在文末，保证与现有内容之间隔一个空行，末尾换行与原文风格一致
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
        return new ConfigPatchResult(candidate, mutations, new PreservationSummary(after.McpServerCount, after.ProjectCount, after.HookSectionCount, after.PluginSectionCount, true));
    }

    /// <summary>结构化读取：根键值、表体（同路径多表体以换行拼接）与 mcp/projects/hooks/plugins 计数。</summary>
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

    /// <summary>校验文本是合法 TOML（非法即抛异常）。</summary>
    public void Validate(string text) => TomlSourceDocument.ParseSyntax(text);

    /// <summary>统计 root.<name> 一级子表的不同名称数（如 mcp_servers.<name>）。</summary>
    private static int CountTopLevelTables(IEnumerable<TomlSourceTable> tables, string root) =>
        tables.Where(item => item.Segments.Count >= 2 && item.Segments[0].Equals(root, StringComparison.Ordinal))
            .Select(item => item.Segments[1])
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>
    /// 从后往前应用全部编辑：先按起点（其次长度）降序排序并检查跨距互不重叠，
    /// 再逐个 Remove + Insert，避免前面的改动使后续偏移失效。
    /// </summary>
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

    /// <summary>把表体换行统一为目标风格（先全部归一为 \n，去首尾空行，再替换）。</summary>
    private static string NormalizeBody(string body, string newLine)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        return normalized.Replace("\n", newLine, StringComparison.Ordinal);
    }

    /// <summary>原始值规范化（仅去首尾空白），用于等值比较。</summary>
    private static string NormalizeRawValue(string value) => value.Trim();

    /// <summary>变更明细里的展示值：敏感键一律显示 &lt;redacted&gt;。</summary>
    private static string DisplayValue(string key, string rawValue) => IsSecret(key) ? "<redacted>" : rawValue.Trim();

    /// <summary>按键名判断是否敏感（token/secret/password/api_key）。</summary>
    private static bool IsSecret(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    /// <summary>按内容判断表体是否包含敏感配置。</summary>
    private static bool ContainsSecret(string value) =>
        value.Contains("experimental_bearer_token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase);

    /// <summary>探测文本换行风格：含 CRLF 用 CRLF，否则 LF。</summary>
    private static string DetectNewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>判断文本是否以换行结尾。</summary>
    private static bool EndsWithNewLine(string text) => EndsWithNewLine(text.AsSpan());

    /// <summary>判断只读文本段是否以换行结尾。</summary>
    private static bool EndsWithNewLine(ReadOnlySpan<char> text) => text.Length > 0 && text[^1] == '\n';

    /// <summary>一次原文替换编辑：起点、长度与替换文本。</summary>
    private sealed record TextEdit(int Start, int Length, string Replacement);
}
