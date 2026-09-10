namespace CodexModelManager.Core.Codex;

/// <summary>
/// Secondary Override 键比较器。键格式为“文件路径|TOML 键路径”：
/// Windows 文件路径不区分大小写，TOML 键段则严格区分，因此两段分别用不同规则比较。
/// </summary>
internal sealed class SecondaryOverrideKeyComparer : IEqualityComparer<string>
{
    /// <summary>单例实例。</summary>
    public static SecondaryOverrideKeyComparer Instance { get; } = new();

    private SecondaryOverrideKeyComparer()
    {
    }

    /// <summary>按键格式分段比较：路径段忽略大小写，键段按序数比较；任一侧缺少分隔符则整体按序数比较。</summary>
    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        int leftSeparator = left.IndexOf('|');
        int rightSeparator = right.IndexOf('|');
        if (leftSeparator < 0 || rightSeparator < 0)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        return left.AsSpan(0, leftSeparator).Equals(right.AsSpan(0, rightSeparator), StringComparison.OrdinalIgnoreCase) && left.AsSpan(leftSeparator + 1).Equals(right.AsSpan(rightSeparator + 1), StringComparison.Ordinal);
    }

    /// <summary>分段计算哈希，与 <see cref="Equals"/> 的分段规则保持一致。</summary>
    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int separator = value.IndexOf('|');
        if (separator < 0)
        {
            return StringComparer.Ordinal.GetHashCode(value);
        }

        return HashCode.Combine(string.GetHashCode(value.AsSpan(0, separator), StringComparison.OrdinalIgnoreCase), string.GetHashCode(value.AsSpan(separator + 1), StringComparison.Ordinal));
    }
}
