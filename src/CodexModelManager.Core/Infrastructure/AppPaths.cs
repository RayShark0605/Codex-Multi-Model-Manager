namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 应用数据目录布局：统一管理本应用在 LocalAppData 下的所有路径。
/// 根目录优先级：构造参数覆盖 &gt; 环境变量 CMM_LOCALAPPDATA_OVERRIDE &gt; 系统 LocalApplicationData。
/// </summary>
public sealed class AppPaths
{
    /// <summary>按上述优先级解析根目录并派生出各子路径。</summary>
    public AppPaths(string? localAppDataOverride = null)
    {
        var localAppData = localAppDataOverride;
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetEnvironmentVariable("CMM_LOCALAPPDATA_OVERRIDE");
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        Root = Path.Combine(localAppData!, "CodexModelManager");
        SettingsPath = Path.Combine(Root, "appsettings.json");
        LogsDirectory = Path.Combine(Root, "logs");
        CatalogDirectory = Path.Combine(Root, "catalogs");
        BinDirectory = Path.Combine(Root, "bin");
        TempDirectory = Path.Combine(Root, "temp");
        TemplateFixDirectory = Path.Combine(Root, "template-fixes");
        TransactionsDirectory = Path.Combine(Root, "transactions");
    }

    /// <summary>应用数据根目录。</summary>
    public string Root { get; }

    /// <summary>appsettings.json 设置文件完整路径。</summary>
    public string SettingsPath { get; }

    /// <summary>日志目录。</summary>
    public string LogsDirectory { get; }

    /// <summary>模型目录缓存目录。</summary>
    public string CatalogDirectory { get; }

    /// <summary>随应用分发的二进制目录。</summary>
    public string BinDirectory { get; }

    /// <summary>临时文件目录。</summary>
    public string TempDirectory { get; }

    /// <summary>LM Studio 模板修复备份目录。</summary>
    public string TemplateFixDirectory { get; }

    /// <summary>切换事务目录。</summary>
    public string TransactionsDirectory { get; }

    /// <summary>确保根目录与全部子目录存在（Directory.CreateDirectory 本身幂等）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CatalogDirectory);
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(TemplateFixDirectory);
        Directory.CreateDirectory(TransactionsDirectory);
    }
}
