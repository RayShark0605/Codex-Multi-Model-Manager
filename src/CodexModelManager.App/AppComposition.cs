using CodexModelManager.App.UI;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Backup;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Providers;
using CodexModelManager.Core.Security;

namespace CodexModelManager.App;

/// <summary>
/// 应用组合根：集中构造并持有全部服务实例（日志、配置、备份、各 Provider、
/// LM Studio 生命周期组件与切换服务），并管理三个 HttpClient 的生命周期。
/// </summary>
internal sealed class AppComposition : IDisposable
{
    /// <summary>Provider 请求（兼容性测试、模型发现）的统一超时。</summary>
    internal static readonly TimeSpan ProviderRequestTimeout = TimeSpan.FromMinutes(3);

    /// <summary>LM Studio 生命周期操作（unload/load/探测）的统一超时。</summary>
    internal static readonly TimeSpan LmStudioLifecycleRequestTimeout = TimeSpan.FromMinutes(30);

    private readonly IAppLogger logger;
    private readonly HttpClient providerHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = ProviderRequestTimeout };
    private readonly HttpClient lmStudioLifecycleHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = LmStudioLifecycleRequestTimeout };
    private readonly HttpClient catalogHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>默认构造：全部依赖取生产实现。</summary>
    public AppComposition()
        : this(null, null, null)
    {
    }

    /// <summary>测试用构造：可注入应用路径、密钥存取与日志。</summary>
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
        GlmCatalog = new GlmCatalogService(HomeProvider, Paths, catalogHttpClient);
        LmStudioPreflight = new LmStudioSwitchPreflight(providerHttpClient, ReadLmStudioSecretSafely);
        GgufReader = new GgufChatTemplateReader();
        TemplateRepair = new PromptTemplateRepairService(GgufReader);
        ModelFileLocator = new LmStudioModelFileLocator();
        TemplateTransactions = new LmStudioTemplateTransactionStore(Paths);
        PerModelDefaults = new LmStudioPerModelDefaultsStore(TemplateRepair, AtomicWriter);
        Switches = new ConfigurationSwitchService(HomeProvider, PatchEngine, AtomicWriter, Backups, OverrideScanner, RuntimeProbe, SettingsRepository, SecretStore, LmStudioPreflight);
    }

    /// <summary>应用数据路径。</summary>
    public AppPaths Paths { get; }

    /// <summary>敏感信息脱敏器。</summary>
    public SecretRedactor Redactor { get; }

    /// <summary>应用日志。</summary>
    internal IAppLogger Logger => logger;

    /// <summary>Codex 主目录解析器。</summary>
    public DefaultCodexHomeProvider HomeProvider { get; }

    /// <summary>TOML 配置补丁引擎。</summary>
    public TomlConfigPatchEngine PatchEngine { get; }

    /// <summary>原子批量写入器。</summary>
    public AtomicBatchWriter AtomicWriter { get; }

    /// <summary>Codex 运行环境探测器。</summary>
    public CodexRuntimeProbe RuntimeProbe { get; }

    /// <summary>应用设置仓库。</summary>
    public AppSettingsRepository SettingsRepository { get; }

    /// <summary>密钥存取。</summary>
    public ISecretStore SecretStore { get; }

    /// <summary>Secondary Override 扫描器。</summary>
    public SecondaryModelOverrideScanner OverrideScanner { get; }

    /// <summary>配置备份服务。</summary>
    public BackupService Backups { get; }

    /// <summary>DeepSeek 模型目录服务。</summary>
    public DeepSeekCatalogService Catalog { get; }

    /// <summary>GLM 模型目录服务。</summary>
    public GlmCatalogService GlmCatalog { get; }

    /// <summary>LM Studio 切换前置检查。</summary>
    public LmStudioSwitchPreflight LmStudioPreflight { get; }

    /// <summary>GGUF 聊天模板读取器。</summary>
    public GgufChatTemplateReader GgufReader { get; }

    /// <summary>提示词模板修复服务。</summary>
    public PromptTemplateRepairService TemplateRepair { get; }

    /// <summary>LM Studio 模型文件定位器。</summary>
    public LmStudioModelFileLocator ModelFileLocator { get; }

    /// <summary>模板事务存储。</summary>
    public LmStudioTemplateTransactionStore TemplateTransactions { get; }

    /// <summary>LM Studio 每模型默认值存储。</summary>
    public LmStudioPerModelDefaultsStore PerModelDefaults { get; }

    /// <summary>配置切换服务。</summary>
    public ConfigurationSwitchService Switches { get; }

    /// <summary>Provider HTTP 客户端超时（测试用）。</summary>
    internal TimeSpan ProviderHttpClientTimeout => providerHttpClient.Timeout;

    /// <summary>LM Studio 生命周期 HTTP 客户端超时（测试用）。</summary>
    internal TimeSpan LmStudioLifecycleHttpClientTimeout => lmStudioLifecycleHttpClient.Timeout;

    /// <summary>按端点与认证需求创建 LM Studio 实例控制器（复用长超时 HttpClient）。</summary>
    public LmStudioInstanceController CreateLmStudioInstanceController(Uri endpoint, bool requiresAuthentication) => new(
        endpoint,
        requiresAuthentication,
        lmStudioLifecycleHttpClient,
        requiresAuthentication ? ReadLmStudioSecretSafely : null,
        RuntimeProbe,
        GgufReader,
        TemplateRepair,
        TemplateTransactions,
        logger,
        PerModelDefaults,
        LmStudioLocalVersionDetector.Detect,
        ModelFileLocator);

    /// <summary>创建主窗体并装配主控制器。</summary>
    public MainForm CreateMainForm()
    {
        var form = new MainForm();
        var controller = new MainController(form, this, logger, providerHttpClient);
        form.AttachController(controller);
        return form;
    }

    /// <summary>释放日志与全部 HttpClient。</summary>
    public void Dispose()
    {
        if (logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }

        providerHttpClient.Dispose();
        lmStudioLifecycleHttpClient.Dispose();
        catalogHttpClient.Dispose();
    }

    /// <summary>安全读取 LM Studio Token：读取失败记警告并返回 null，绝不向上抛异常。</summary>
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
