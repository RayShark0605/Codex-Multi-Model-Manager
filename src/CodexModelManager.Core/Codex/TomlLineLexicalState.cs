namespace CodexModelManager.Core.Codex;

/// <summary>
/// Tracks TOML multiline strings so line-oriented scanners never interpret
/// apparent tables or assignments from string contents as configuration.
/// </summary>
internal sealed class TomlLineLexicalState
{
    private MultilineStringKind multilineString;

    public bool IsCodeLineAndAdvance(string line)
    {
        bool startsInCode = multilineString == MultilineStringKind.None;
        int index = 0;
        while (index < line.Length)
        {
            if (multilineString == MultilineStringKind.Basic)
            {
                int closing = FindBasicMultilineClosing(line, index);
                if (closing < 0)
                {
                    return startsInCode;
                }

                multilineString = MultilineStringKind.None;
                index = closing + 3;
                continue;
            }

            if (multilineString == MultilineStringKind.Literal)
            {
                int closing = line.IndexOf("'''", index, StringComparison.Ordinal);
                if (closing < 0)
                {
                    return startsInCode;
                }

                multilineString = MultilineStringKind.None;
                index = closing + 3;
                continue;
            }

            if (line[index] == '#')
            {
                break;
            }

            if (line.AsSpan(index).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                multilineString = MultilineStringKind.Basic;
                index += 3;
                continue;
            }

            if (line.AsSpan(index).StartsWith("'''", StringComparison.Ordinal))
            {
                multilineString = MultilineStringKind.Literal;
                index += 3;
                continue;
            }

            if (line[index] == '"')
            {
                index = SkipBasicString(line, index + 1);
                continue;
            }

            if (line[index] == '\'')
            {
                int closing = line.IndexOf('\'', index + 1);
                index = closing < 0 ? line.Length : closing + 1;
                continue;
            }

            index++;
        }

        return startsInCode;
    }

    private static int FindBasicMultilineClosing(string line, int start)
    {
        for (int index = start; index <= line.Length - 3; index++)
        {
            if (!line.AsSpan(index).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                continue;
            }

            int slashCount = 0;
            for (int previous = index - 1; previous >= 0 && line[previous] == '\\'; previous--)
            {
                slashCount++;
            }

            if (slashCount % 2 == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int SkipBasicString(string line, int index)
    {
        bool escaped = false;
        while (index < line.Length)
        {
            char current = line[index++];
            if (escaped)
            {
                escaped = false;
            }
            else if (current == '\\')
            {
                escaped = true;
            }
            else if (current == '"')
            {
                break;
            }
        }

        return index;
    }

    private enum MultilineStringKind
    {
        None,
        Basic,
        Literal,
    }
}
