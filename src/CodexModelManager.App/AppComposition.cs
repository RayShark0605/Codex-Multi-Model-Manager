using CodexModelManager.App.UI;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Backup;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Providers;
using CodexModelManager.Core.Security;

namespace CodexModelManager.App;

internal sealed class AppComposition : IDisposable
{
    private readonly IAppLogger logger;
    private readonly HttpClient providerHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient catalogHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public AppComposition()
        : this(null, null, null)
    {
    }

    internal AppComposition(AppPaths? paths, ISecretStore? secretStore, IAppLogger? logger)
    {
        Paths = paths ?? new AppPaths();
        Paths.EnsureDirectories();
        Redactor = new SecretRedactor();
        this.logger = logger ?? new AppLogger(Paths, Redactor);
        HomeProvider = new DefaultCodexHomeProvider();
        PatchEngine = new TomlConfigPatchEngine();
        AtomicWriter = new AtomicBatchWriter();
        RuntimeProbe = new CodexRuntimeProbe(HomeProvider, PatchEngine);
        SettingsRepository = new AppSettingsRepository(Paths);
        SecretStore = secretStore ?? new WindowsCredentialStore();
        OverrideScanner = new SecondaryModelOverrideScanner(PatchEngine);
        Backups = new BackupService(HomeProvider, AtomicWriter, PatchEngine);
        Catalog = new DeepSeekCatalogService(HomeProvider, Paths, catalogHttpClient);
        LmStudioPreflight = new LmStudioSwitchPreflight(providerHttpClient, ReadLmStudioSecretSafely);
        GgufReader = new GgufChatTemplateReader();
        TemplateRepair = new PromptTemplateRepairService(GgufReader);
        ModelFileLocator = new LmStudioModelFileLocator();
        TemplateTransactions = new LmStudioTemplateTransactionStore(Paths);
        PerModelDefaults = new LmStudioPerModelDefaultsStore(TemplateRepair, AtomicWriter);
        Switches = new ConfigurationSwitchService(HomeProvider, PatchEngine, AtomicWriter, Backups, OverrideScanner, RuntimeProbe, SettingsRepository, SecretStore, LmStudioPreflight);
    }

    public AppPaths Paths { get; }
    public SecretRedactor Redactor { get; }
    internal IAppLogger Logger => logger;
    public DefaultCodexHomeProvider HomeProvider { get; }
    public TomlConfigPatchEngine PatchEngine { get; }
    public AtomicBatchWriter AtomicWriter { get; }
    public CodexRuntimeProbe RuntimeProbe { get; }
    public AppSettingsRepository SettingsRepository { get; }
    public ISecretStore SecretStore { get; }
    public SecondaryModelOverrideScanner OverrideScanner { get; }
    public BackupService Backups { get; }
    public DeepSeekCatalogService Catalog { get; }
    public LmStudioSwitchPreflight LmStudioPreflight { get; }
    public GgufChatTemplateReader GgufReader { get; }
    public PromptTemplateRepairService TemplateRepair { get; }
    public LmStudioModelFileLocator ModelFileLocator { get; }
    public LmStudioTemplateTransactionStore TemplateTransactions { get; }
    public LmStudioPerModelDefaultsStore PerModelDefaults { get; }
    public ConfigurationSwitchService Switches { get; }

    public LmStudioInstanceController CreateLmStudioInstanceController(Uri endpoint, bool requiresAuthentication) => new(
        endpoint,
        requiresAuthentication,
        providerHttpClient,
        requiresAuthentication ? ReadLmStudioSecretSafely : null,
        RuntimeProbe,
        GgufReader,
        TemplateRepair,
        TemplateTransactions,
        logger,
        PerModelDefaults,
        LmStudioLocalVersionDetector.Detect,
        ModelFileLocator);

    public MainForm CreateMainForm()
    {
        var form = new MainForm();
        var controller = new MainController(form, this, logger, providerHttpClient);
        form.AttachController(controller);
        return form;
    }

    public void Dispose()
    {
        if (logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }
        providerHttpClient.Dispose();
        catalogHttpClient.Dispose();
    }

    private string? ReadLmStudioSecretSafely()
    {
        try
        {
            return SecretStore.Read(CredentialNames.LmStudio);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.Warning($"Credential Manager 读取 LM Studio Token 失败 ({exception.GetType().Name})。");
            return null;
        }
    }
}
