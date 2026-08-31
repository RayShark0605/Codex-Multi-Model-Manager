namespace CodexModelManager.Core.LmStudio;

internal static class LmStudioPerModelDefaultsCompatibility
{
    internal const string SupportedVersionFamilies = "0.4.21.x / 0.4.23.x";

    internal static bool IsSupportedVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        string numericVersion = versionText.Trim();
        int metadataSeparator = numericVersion.IndexOf('+');
        if (metadataSeparator >= 0)
        {
            string buildMetadata = numericVersion[(metadataSeparator + 1)..];
            if (buildMetadata.Length == 0 || buildMetadata.Any(character => !char.IsAsciiDigit(character)) || numericVersion.IndexOf('+', metadataSeparator + 1) >= 0)
            {
                return false;
            }

            numericVersion = numericVersion[..metadataSeparator];
        }

        if (numericVersion.Contains('-') || !Version.TryParse(numericVersion, out Version? version))
        {
            return false;
        }

        return version.Major == 0 && version.Minor == 4 && version.Build is 21 or 23;
    }
}
