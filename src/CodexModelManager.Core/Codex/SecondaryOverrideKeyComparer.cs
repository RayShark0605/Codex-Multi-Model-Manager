namespace CodexModelManager.Core.Codex;

/// <summary>Windows file paths are case-insensitive; TOML key segments are not.</summary>
internal sealed class SecondaryOverrideKeyComparer : IEqualityComparer<string>
{
    public static SecondaryOverrideKeyComparer Instance { get; } = new();

    private SecondaryOverrideKeyComparer()
    {
    }

    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        int leftSeparator = left.IndexOf('|');
        int rightSeparator = right.IndexOf('|');
        if (leftSeparator < 0 || rightSeparator < 0) return string.Equals(left, right, StringComparison.Ordinal);
        return left.AsSpan(0, leftSeparator).Equals(right.AsSpan(0, rightSeparator), StringComparison.OrdinalIgnoreCase) &&
            left.AsSpan(leftSeparator + 1).Equals(right.AsSpan(rightSeparator + 1), StringComparison.Ordinal);
    }

    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int separator = value.IndexOf('|');
        if (separator < 0) return StringComparer.Ordinal.GetHashCode(value);
        return HashCode.Combine(string.GetHashCode(value.AsSpan(0, separator), StringComparison.OrdinalIgnoreCase),
            string.GetHashCode(value.AsSpan(separator + 1), StringComparison.Ordinal));
    }
}
