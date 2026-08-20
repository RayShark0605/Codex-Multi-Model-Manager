namespace CodexModelManager.Core.Infrastructure;

public sealed class AppPaths
{
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

    public string Root { get; }

    public string SettingsPath { get; }

    public string LogsDirectory { get; }

    public string CatalogDirectory { get; }

    public string BinDirectory { get; }

    public string TempDirectory { get; }

    public string TemplateFixDirectory { get; }

    public string TransactionsDirectory { get; }

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
