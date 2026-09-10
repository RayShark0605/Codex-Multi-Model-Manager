using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Codex;

/// <summary>
/// Secondary Model Override 扫描器：递归扫描主 config.toml 及其引用的
/// agent config、*.config.toml 与 projects 配置，找出所有二级模型覆盖项
/// （profiles/agents/memories 的 model、review_model 等各类 *_model 键）。
/// </summary>
public sealed class SecondaryModelOverrideScanner : ISecondaryModelOverrideScanner
{
    private readonly IConfigPatchEngine validator;

    /// <summary>以配置校验器构造（引用配置解析前先验证 TOML 合法性）。</summary>
    public SecondaryModelOverrideScanner(IConfigPatchEngine validator) => this.validator = validator;

    /// <summary>从主配置开始扫描并返回全部覆盖项（含引用文件里的项与扫描错误项）。</summary>
    public async Task<IReadOnlyList<SecondaryModelOverride>> ScanAsync(string configPath, CancellationToken cancellationToken = default)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        List<SecondaryModelOverride> results = [];
        await ScanFileAsync(Path.GetFullPath(configPath), true, visited, results, cancellationToken).ConfigureAwait(false);
        return results;
    }

    /// <summary>扫描单个文件：解析 TOML、收集 model_provider 映射与二级模型键，再递归引用的配置。</summary>
    private async Task ScanFileAsync(string path, bool isPrimary, HashSet<string> visited, List<SecondaryModelOverride> results, CancellationToken cancellationToken)
    {
        if (!visited.Add(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            if (!isPrimary)
            {
                results.Add(new SecondaryModelOverride(path, "<scan_error>", "<unknown>", null, false, false, "引用的配置不存在或当前进程无权确认其存在，未扫描 model override。"));
            }

            return;
        }

        TomlSourceDocument document;
        try
        {
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            validator.Validate(text);
            document = TomlSourceDocument.Parse(text);
        }
        catch (Exception exception) when (!isPrimary && exception is
            InvalidDataException or ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException or IOException)
        {
            results.Add(new SecondaryModelOverride(path, "<scan_error>", "<unknown>", null, false, false, $"引用的配置不可读或 TOML 无效，未扫描其 model override（{exception.GetType().Name}）。"));
            return;
        }

        Dictionary<string, string> providersByTable = new(StringComparer.Ordinal);
        List<(string Table, string Relative)> referencedConfigs = [];
        // 仅主配置解析 projects.<path> 表得到项目根（引用配置不再递归 projects）
        List<string> projectRoots = isPrimary ? document.Tables.Select(table => TryParseProjectRoot(table.Segments)).OfType<string>().ToList() : [];
        foreach (TomlSourceAssignment assignment in document.Assignments.Where(item => item.StringValue is not null && !item.IsArrayMember))
        {
            string leafKey = assignment.Segments[^1];
            if (leafKey == "model_provider")
            {
                providersByTable[assignment.OwnerPath] = assignment.StringValue!;
            }
            else if (leafKey == "config_file" && assignment.Segments.Count >= 3 && assignment.Segments[0] == "agents")
            {
                referencedConfigs.Add((assignment.OwnerPath, assignment.StringValue!));
            }
        }

        foreach (IGrouping<string, TomlSourceAssignment> group in document.Assignments.Where(item => item.StringValue is not null && IsSecondaryModelKey(item.Segments[^1], item.OwnerPath, isPrimary)).GroupBy(item => item.Path, StringComparer.Ordinal))
        {
            TomlSourceAssignment assignment = group.First();
            // 同一路径出现多次（或位于数组表）时无法唯一定位，标记为禁止自动修改
            bool ambiguous = assignment.IsArrayMember || group.Count() != 1;
            string? provider = providersByTable.GetValueOrDefault(assignment.OwnerPath);
            string model = assignment.StringValue!;
            results.Add(new SecondaryModelOverride(
                path,
                assignment.Path,
                model,
                provider,
                IsPotentialCloud(model, provider),
                isPrimary && !ambiguous,
                ambiguous ? "Array-table override 无法通过当前选择键唯一定位，禁止自动修改。" : $"{Path.GetFileName(path)}:{assignment.LineNumber}",
                ambiguous ? null : assignment.RawValue));
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
            // 同目录下的 *.config.toml 视为 Codex 的 profile 配置，一并扫描
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

    /// <summary>
    /// 判断某键是否二级模型键：非主配置里任何 model 键都算；
    /// 主配置里仅 profiles./agents./memories. 前缀下的 model 与各类 *_model 键算。
    /// </summary>
    private static bool IsSecondaryModelKey(string key, string table, bool isPrimary)
    {
        if (key.Equals("model", StringComparison.Ordinal))
        {
            if (!isPrimary)
            {
                return true;
            }

            return table.StartsWith("profiles.", StringComparison.Ordinal) || table.StartsWith("agents.", StringComparison.Ordinal) || table.StartsWith("memories.", StringComparison.Ordinal);
        }

        return key.Equals("review_model", StringComparison.Ordinal) ||
               key.Equals("default_subagent_model", StringComparison.Ordinal) ||
               key.Equals("extract_model", StringComparison.Ordinal) ||
               key.Equals("consolidation_model", StringComparison.Ordinal) ||
               key.EndsWith("_model", StringComparison.Ordinal);
    }

    /// <summary>推断覆盖项是否会命中云端请求：LM Studio 视为本地；其余按模型名特征或 Provider 名判断。</summary>
    private static bool IsPotentialCloud(string model, string? provider)
    {
        if (provider is not null && provider.Contains("lmstudio", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string lower = model.ToLowerInvariant();
        return lower.StartsWith("gpt-", StringComparison.Ordinal) ||
               lower.Contains("deepseek", StringComparison.Ordinal) ||
               lower.Contains("claude", StringComparison.Ordinal) ||
               lower.Contains("gemini", StringComparison.Ordinal) ||
               (provider is not null && !provider.Contains("local", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析 projects.&lt;path&gt; 表路径中的项目根；形状不符返回 null。</summary>
    private static string? TryParseProjectRoot(IReadOnlyList<string> segments)
    {
        return segments.Count == 2 && segments[0].Equals("projects", StringComparison.Ordinal) ? segments[1] : null;
    }
}
