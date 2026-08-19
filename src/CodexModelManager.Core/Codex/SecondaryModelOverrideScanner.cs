using System.Text.RegularExpressions;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

public sealed partial class SecondaryModelOverrideScanner : ISecondaryModelOverrideScanner
{
    private readonly IConfigPatchEngine validator;

    public SecondaryModelOverrideScanner(IConfigPatchEngine validator) => this.validator = validator;

    public async Task<IReadOnlyList<SecondaryModelOverride>> ScanAsync(string configPath, CancellationToken cancellationToken = default)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        List<SecondaryModelOverride> results = [];
        await ScanFileAsync(Path.GetFullPath(configPath), true, visited, results, cancellationToken).ConfigureAwait(false);
        return results;
    }

    private async Task ScanFileAsync(
        string path,
        bool isPrimary,
        HashSet<string> visited,
        List<SecondaryModelOverride> results,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(path) || !File.Exists(path)) return;
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            validator.Validate(text);
        }
        catch (InvalidDataException) when (!isPrimary)
        {
            results.Add(new SecondaryModelOverride(path, "<scan_error>", "<unknown>", null, false, false, "引用的配置 TOML 无效，未扫描其 model override。"));
            return;
        }

        IReadOnlyList<string> currentTableSegments = [];
        Dictionary<string, string> providersByTable = new(StringComparer.Ordinal);
        List<(string KeyPath, string Table, string Model, string RawValue, int Line)> pendingModels = [];
        List<(string Table, string Relative)> referencedConfigs = [];
        List<string> projectRoots = [];
        int lineNumber = 0;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            Match header = TableHeaderRegex().Match(line);
            if (header.Success)
            {
                string rawTable = header.Groups["table"].Success ? header.Groups["table"].Value : header.Groups["array"].Value;
                currentTableSegments = TomlDottedKey.ParseSegments(rawTable);
                if (isPrimary && TryParseProjectRoot(currentTableSegments) is string projectRoot) projectRoots.Add(projectRoot);
                continue;
            }

            Match assignment = AssignmentRegex().Match(line);
            if (!assignment.Success) continue;
            IReadOnlyList<string> keySegments = TomlDottedKey.ParseSegments(assignment.Groups["key"].Value);
            string[] fullSegments = [.. currentTableSegments, .. keySegments];
            string keyPath = TomlDottedKey.Canonical(fullSegments);
            string ownerTable = TomlDottedKey.Canonical(fullSegments.Take(fullSegments.Length - 1));
            string leafKey = fullSegments[^1];
            string value = ReadString(assignment);
            if (leafKey.Equals("model_provider", StringComparison.Ordinal))
            {
                providersByTable[ownerTable] = value;
                continue;
            }

            if (leafKey.Equals("config_file", StringComparison.Ordinal) && ownerTable.StartsWith("agents.", StringComparison.Ordinal))
            {
                referencedConfigs.Add((ownerTable, value));
                continue;
            }

            if (!IsSecondaryModelKey(leafKey, ownerTable, isPrimary)) continue;
            pendingModels.Add((keyPath, ownerTable, value, assignment.Groups["quoted"].Value, lineNumber));
        }

        foreach ((string keyPath, string ownerTable, string model, string rawValue, int line) in pendingModels)
        {
            string? provider = providersByTable.GetValueOrDefault(ownerTable);
            results.Add(new SecondaryModelOverride(
                path,
                keyPath,
                model,
                provider,
                IsPotentialCloud(model, provider),
                isPrimary,
                $"{Path.GetFileName(path)}:{line}",
                rawValue));
        }

        string? baseDirectory = Path.GetDirectoryName(path);
        foreach ((string table, string relative) in referencedConfigs)
        {
            try
            {
                string referenced = Path.IsPathRooted(relative) ? relative : Path.Combine(baseDirectory!, relative);
                await ScanFileAsync(Path.GetFullPath(referenced), false, visited, results, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                results.Add(new SecondaryModelOverride(path, table + ".config_file", relative, null, false, false, "引用的 agent config 路径无效。"));
            }
        }

        if (isPrimary && baseDirectory is not null)
        {
            foreach (string profile in Directory.EnumerateFiles(baseDirectory, "*.config.toml", SearchOption.TopDirectoryOnly))
            {
                await ScanFileAsync(profile, false, visited, results, cancellationToken).ConfigureAwait(false);
            }

            foreach (string projectRoot in projectRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string projectConfig = Path.Combine(projectRoot, ".codex", "config.toml");
                    await ScanFileAsync(Path.GetFullPath(projectConfig), false, visited, results, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException or IOException)
                {
                    results.Add(new SecondaryModelOverride(path, "projects.<path>.config", "<unknown>", null, false, false, "Project 配置路径不可读，未自动修改。"));
                }
            }
        }
    }

    private static bool IsSecondaryModelKey(string key, string table, bool isPrimary)
    {
        if (key.Equals("model", StringComparison.Ordinal))
        {
            if (!isPrimary) return true;
            return table.StartsWith("profiles.", StringComparison.Ordinal) ||
                   table.StartsWith("agents.", StringComparison.Ordinal) ||
                   table.StartsWith("memories.", StringComparison.Ordinal);
        }

        return key.Equals("review_model", StringComparison.Ordinal) ||
               key.Equals("default_subagent_model", StringComparison.Ordinal) ||
               key.Equals("extract_model", StringComparison.Ordinal) ||
               key.Equals("consolidation_model", StringComparison.Ordinal) ||
               key.EndsWith("_model", StringComparison.Ordinal);
    }

    private static bool IsPotentialCloud(string model, string? provider)
    {
        if (provider is not null && provider.Contains("lmstudio", StringComparison.OrdinalIgnoreCase)) return false;
        string lower = model.ToLowerInvariant();
        return lower.StartsWith("gpt-", StringComparison.Ordinal) ||
               lower.Contains("deepseek", StringComparison.Ordinal) ||
               lower.Contains("claude", StringComparison.Ordinal) ||
               lower.Contains("gemini", StringComparison.Ordinal) ||
               (provider is not null && !provider.Contains("local", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryParseProjectRoot(IReadOnlyList<string> segments)
    {
        return segments.Count == 2 && segments[0].Equals("projects", StringComparison.Ordinal)
            ? segments[1]
            : null;
    }

    private static string ReadString(Match assignment)
    {
        if (assignment.Groups["literal"].Success) return assignment.Groups["literal"].Value;
        string value = assignment.Groups["basic"].Value;
        string raw = '"' + value + '"';
        try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? value; } catch (System.Text.Json.JsonException) { return value; }
    }

    [GeneratedRegex("^\\s*(?:\\[\\[(?<array>[^]]+)\\]\\]|\\[(?<table>[^]]+)\\])\\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableHeaderRegex();

    [GeneratedRegex("""^\s*(?<key>(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*')(?:\s*\.\s*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*'))*)\s*=\s*(?<quoted>"(?<basic>(?:\\.|[^"\\])*)"|'(?<literal>[^']*)')\s*(?:#.*)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();
}
