using System.Text;
using System.Text.Json;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// TOML 点分键（dotted key）解析与规范化：识别基本串/字面串引号包裹的键段，
/// 解码后按段重组为规范形式（能裸写就裸写，否则以 JSON 串编码）。
/// </summary>
internal static class TomlDottedKey
{
    /// <summary>把点分键文本拆成键段列表：引号内的“.”不作为分隔符，转义按 TOML 规则处理。</summary>
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
                // 基本串（双引号）内：保留转义状态，遇未转义的闭引号退出
                current.Append(character);
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
                // 字面串（单引号）内：无转义，遇闭引号退出
                current.Append(character);
                if (character == '\'')
                {
                    literal = false;
                }

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

    /// <summary>把点分键文本解析为键段后重组为规范形式。</summary>
    public static string Canonical(string value) => Canonical(ParseSegments(value));

    /// <summary>把键段列表重组为规范点分键：每段能裸写则裸写，否则 JSON 编码。</summary>
    public static string Canonical(IEnumerable<string> segments) => string.Join('.', segments.Select(EncodeSegment));

    /// <summary>解码单个键段：字面串去引号；基本串借助 TOML 解析器解码（支持 JSON 没有的 \UXXXXXXXX 等转义）。</summary>
    private static string DecodeSegment(string value)
    {
        string segment = value.Trim();
        if (segment.Length >= 2 && segment[0] == '\'' && segment[^1] == '\'')
        {
            return segment[1..^1];
        }

        if (segment.Length >= 2 && segment[0] == '"' && segment[^1] == '"')
        {
            // TOML 支持 JSON 不支持的转义（含 \UXXXXXXXX），交给 TOML 解析器解码更稳妥
            return TomlSourceDocument.GetSegments(TomlSourceDocument.ParseSyntax(segment + " = 0").KeyValues.First().Key)[0];
        }

        return segment;
    }

    /// <summary>编码单个键段：仅含字母/数字/下划线/连字符且非空时原样输出，否则 JSON 序列化。</summary>
    private static string EncodeSegment(string value) => value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-') ? value : JsonSerializer.Serialize(value);
}
