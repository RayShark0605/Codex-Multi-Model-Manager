using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// A read-only index into the original TOML text. Tomlyn owns grammar and string
/// decoding; edits always address original source spans, never serialized tables.
/// </summary>
internal sealed class TomlSourceDocument
{
    private TomlSourceDocument(string text, IReadOnlyList<TomlSourceAssignment> assignments, IReadOnlyList<TomlSourceTable> tables)
    {
        Text = text;
        Assignments = assignments;
        Tables = tables;
    }

    public string Text { get; }
    public IReadOnlyList<TomlSourceAssignment> Assignments { get; }
    public IReadOnlyList<TomlSourceTable> Tables { get; }
    public bool HasTrailingNewLine => Text.EndsWith('\n');

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
            // Parser messages and inner exceptions may quote an entire source line,
            // including bearer tokens. Only fixed classifications and coordinates
            // are allowed to leave this boundary.
            throw InvalidSyntax(exception.Diagnostics);
        }

        if (document.HasErrors) throw InvalidSyntax(document.Diagnostics);
        return document;
    }

    private static InvalidDataException InvalidSyntax(DiagnosticsBag? diagnostics)
    {
        string locations = diagnostics is null ? string.Empty : string.Join(", ", diagnostics.Take(8).Select(item => $"{item.Span.Start.Line + 1}:{item.Span.Start.Column + 1}"));
        return new InvalidDataException("config.toml 语法或语义无效" + (locations.Length == 0 ? "。" : $"（行:列 {locations}）。"));
    }

    public static TomlSourceDocument Parse(string text)
    {
        DocumentSyntax document = ParseSyntax(text);
        List<TomlSourceAssignment> assignments = [];
        List<TomlSourceTable> tables = [];
        foreach (KeyValueSyntax item in document.KeyValues) assignments.Add(CreateAssignment(text, item, [], false));

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
            foreach (KeyValueSyntax item in table.Items) assignments.Add(CreateAssignment(text, item, segments, isArrayMember));
        }

        return new TomlSourceDocument(text, assignments, tables);
    }

    internal static IReadOnlyList<string> GetSegments(KeySyntax? key)
    {
        KeySyntax syntax = Require(key);
        return new[] { DecodeSegment(syntax.Key) }.Concat(syntax.DotKeys.Select(item => DecodeSegment(item.Key))).ToArray();
    }

    private static string DecodeSegment(BareKeyOrStringValueSyntax? segment) => segment switch
    {
        BareKeySyntax bare => Require(bare.Key).Text ?? throw new InvalidDataException("TOML key 缺少文本。"),
        StringValueSyntax quoted => quoted.Value ?? throw new InvalidDataException("TOML key 缺少字符串值。"),
        _ => throw new InvalidDataException("不支持的 TOML key 类型。"),
    };

    internal static bool IsSameOrDescendant(IReadOnlyList<string> candidate, IReadOnlyList<string> parent) =>
        candidate.Count >= parent.Count && parent.Select((segment, index) => segment.Equals(candidate[index], StringComparison.Ordinal)).All(equal => equal);

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

    private static T Require<T>(T? node) where T : SyntaxNode => node ?? throw new InvalidDataException("TOML 语法节点不完整。");

    private static int LineStart(string text, int offset) => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    private static int LineEnd(string text, int offset)
    {
        int newline = text.IndexOf('\n', offset);
        return newline < 0 ? text.Length : newline + 1;
    }
}

internal sealed record TomlSourceAssignment(
    IReadOnlyList<string> Segments, bool IsDocumentRoot, int Start, int Length, int ValueStart, int ValueLength,
    string RawValue, string? StringValue, int LineNumber, bool IsArrayMember)
{
    public string Path => TomlDottedKey.Canonical(Segments);
    public string OwnerPath => TomlDottedKey.Canonical(Segments.Take(Segments.Count - 1));
}

internal sealed record TomlSourceTable(IReadOnlyList<string> Segments, int Start, int Length, string Body, bool IsArrayMember)
{
    public string Path => TomlDottedKey.Canonical(Segments);
}
