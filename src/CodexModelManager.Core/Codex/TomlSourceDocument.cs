using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// TOML 原文的只读索引：语法与字符串解码交给 Tomlyn，
/// 本类只记录各赋值与表在原文中的跨距（span），后续编辑一律基于原文跨距而非序列化后的表结构。
/// </summary>
internal sealed class TomlSourceDocument
{
    private TomlSourceDocument(string text, IReadOnlyList<TomlSourceAssignment> assignments, IReadOnlyList<TomlSourceTable> tables)
    {
        Text = text;
        Assignments = assignments;
        Tables = tables;
    }

    /// <summary>原始 TOML 文本。</summary>
    public string Text { get; }

    /// <summary>全部键值赋值（含所属表路径与原文位置）。</summary>
    public IReadOnlyList<TomlSourceAssignment> Assignments { get; }

    /// <summary>全部表（含表头跨距与表体原文）。</summary>
    public IReadOnlyList<TomlSourceTable> Tables { get; }

    /// <summary>文本是否以换行结尾。</summary>
    public bool HasTrailingNewLine => Text.EndsWith('\n');

    /// <summary>严格解析 TOML 文本为 Tomlyn 语法树；任何错误都转成不含原文内容的 InvalidDataException。</summary>
    internal static DocumentSyntax ParseSyntax(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        DocumentSyntax document;
        try
        {
            document = SyntaxParser.ParseStrict(text, "config.toml", true);
        }
        catch (Tomlyn.TomlException exception)
        {
            // 解析器的报错信息与内部异常可能引用整行原文（含 bearer token），
            // 离开本边界的信息只允许固定分类与行:列坐标。
            throw InvalidSyntax(exception.Diagnostics);
        }

        if (document.HasErrors)
        {
            throw InvalidSyntax(document.Diagnostics);
        }

        return document;
    }

    /// <summary>组装只含分类与坐标（前 8 处“行:列”）的语法错误异常。</summary>
    private static InvalidDataException InvalidSyntax(DiagnosticsBag? diagnostics)
    {
        string locations = diagnostics is null ? string.Empty : string.Join(", ", diagnostics.Take(8).Select(item => $"{item.Span.Start.Line + 1}:{item.Span.Start.Column + 1}"));
        return new InvalidDataException("config.toml 语法或语义无效" + (locations.Length == 0 ? "。" : $"（行:列 {locations}）。"));
    }

    /// <summary>
    /// 解析并建立索引：文档根键值逐项登记；每个表的表体从闭括号行尾到下一表头（或文末），
    /// 同时登记表内全部键值；数组表成员会标注 IsArrayMember。
    /// </summary>
    public static TomlSourceDocument Parse(string text)
    {
        DocumentSyntax document = ParseSyntax(text);
        List<TomlSourceAssignment> assignments = [];
        List<TomlSourceTable> tables = [];
        foreach (KeyValueSyntax item in document.KeyValues)
        {
            assignments.Add(CreateAssignment(text, item, [], false));
        }

        TableSyntaxBase[] syntaxTables = document.Tables.ToArray();
        IReadOnlyList<string>[] arrayPaths = syntaxTables.OfType<TableArraySyntax>().Select(table => GetSegments(table.Name)).ToArray();
        for (int index = 0; index < syntaxTables.Length; index++)
        {
            TableSyntaxBase table = syntaxTables[index];
            IReadOnlyList<string> segments = GetSegments(table.Name);
            int start = LineStart(text, Require(table.OpenBracket).Span.Offset);
            int end = index + 1 < syntaxTables.Length ? LineStart(text, Require(syntaxTables[index + 1].OpenBracket).Span.Offset) : text.Length;
            SyntaxToken closeBracket = Require(table.CloseBracket);
            int bodyStart = LineEnd(text, closeBracket.Span.Offset + closeBracket.Span.Length);
            bool isArrayMember = arrayPaths.Any(path => IsSameOrDescendant(segments, path));
            tables.Add(new TomlSourceTable(segments, start, end - start, text[bodyStart..end], isArrayMember));
            foreach (KeyValueSyntax item in table.Items)
            {
                assignments.Add(CreateAssignment(text, item, segments, isArrayMember));
            }
        }

        return new TomlSourceDocument(text, assignments, tables);
    }

    /// <summary>读取键语法节点的全部段（裸键或字符串键，含点分段）。</summary>
    internal static IReadOnlyList<string> GetSegments(KeySyntax? key)
    {
        KeySyntax syntax = Require(key);
        return new[] { DecodeSegment(syntax.Key) }.Concat(syntax.DotKeys.Select(item => DecodeSegment(item.Key))).ToArray();
    }

    /// <summary>解码单个键段：裸键取文本，字符串键取解码后的值。</summary>
    private static string DecodeSegment(BareKeyOrStringValueSyntax? segment) => segment switch
    {
        BareKeySyntax bare => Require(bare.Key).Text ?? throw new InvalidDataException("TOML key 缺少文本。"),
        StringValueSyntax quoted => quoted.Value ?? throw new InvalidDataException("TOML key 缺少字符串值。"),
        _ => throw new InvalidDataException("不支持的 TOML key 类型。"),
    };

    /// <summary>判断 candidate 是否等于 parent 或为其子孙路径。</summary>
    internal static bool IsSameOrDescendant(IReadOnlyList<string> candidate, IReadOnlyList<string> parent) =>
        candidate.Count >= parent.Count && parent.Select((segment, index) => segment.Equals(candidate[index], StringComparison.Ordinal)).All(equal => equal);

    /// <summary>登记一条键值赋值：全路径、是否文档根、原文行范围、值跨距与原始值文本等。</summary>
    private static TomlSourceAssignment CreateAssignment(string text, KeyValueSyntax item, IReadOnlyList<string> tableSegments, bool isArrayMember)
    {
        IReadOnlyList<string> keySegments = GetSegments(item.Key);
        string[] fullSegments = [.. tableSegments, .. keySegments];
        BareKeyOrStringValueSyntax firstKey = Require(Require(item.Key).Key);
        ValueSyntax value = Require(item.Value);
        int start = LineStart(text, firstKey.Span.Offset);
        int valueStart = value.Span.Offset;
        int valueLength = value.Span.Length;
        int end = LineEnd(text, valueStart + valueLength);
        return new TomlSourceAssignment(fullSegments, tableSegments.Count == 0, start, end - start, valueStart, valueLength,
            text.Substring(valueStart, valueLength), (value as StringValueSyntax)?.Value, firstKey.Span.Start.Line + 1, isArrayMember);
    }

    /// <summary>断言语法节点存在，否则按“语法节点不完整”失败。</summary>
    private static T Require<T>(T? node) where T : SyntaxNode => node ?? throw new InvalidDataException("TOML 语法节点不完整。");

    /// <summary>返回 offset 所在行的行首位置。</summary>
    private static int LineStart(string text, int offset) => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>返回 offset 所在行的行尾位置（含换行符；无换行则为文本末尾）。</summary>
    private static int LineEnd(string text, int offset)
    {
        int newline = text.IndexOf('\n', offset);
        return newline < 0 ? text.Length : newline + 1;
    }
}

/// <summary>一条键值赋值的原文索引项。</summary>
internal sealed record TomlSourceAssignment(
    IReadOnlyList<string> Segments, bool IsDocumentRoot, int Start, int Length, int ValueStart, int ValueLength,
    string RawValue, string? StringValue, int LineNumber, bool IsArrayMember)
{
    /// <summary>规范化点分键路径。</summary>
    public string Path => TomlDottedKey.Canonical(Segments);

    /// <summary>所属表（去掉最后一段键）的路径。</summary>
    public string OwnerPath => TomlDottedKey.Canonical(Segments.Take(Segments.Count - 1));
}

/// <summary>一个表的原文索引项：表头跨距与表体原文。</summary>
internal sealed record TomlSourceTable(IReadOnlyList<string> Segments, int Start, int Length, string Body, bool IsArrayMember)
{
    /// <summary>规范化表路径。</summary>
    public string Path => TomlDottedKey.Canonical(Segments);
}
