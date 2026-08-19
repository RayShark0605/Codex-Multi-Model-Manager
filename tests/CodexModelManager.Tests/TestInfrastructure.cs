using System.Net;
using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Backup;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Security;

namespace CodexModelManager.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexModelManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}

internal sealed class TestCodexHomeProvider : ICodexHomeProvider
{
    public TestCodexHomeProvider(string path)
    {
        Home = System.IO.Path.GetFullPath(path);
        string real = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        if (Home.Equals(real, StringComparison.OrdinalIgnoreCase) || Home.StartsWith(real + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tests must never access the real CODEX_HOME.");
        }

        Directory.CreateDirectory(Home);
    }

    public string Home { get; }

    public string GetCodexHome() => Home;
}

internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public void Save(string targetName, ReadOnlySpan<char> secret) => values[targetName] = secret.ToString();
    public string? Read(string targetName) => values.GetValueOrDefault(targetName);
    public bool Exists(string targetName) => values.ContainsKey(targetName);
    public void Delete(string targetName) => values.Remove(targetName);
}

internal sealed class FakeRuntimeProbe(TestCodexHomeProvider home, bool running = false, string version = "codex-cli 0.148.0") : ICodexRuntimeProbe
{
    public Task<CodexEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CodexEnvironmentInfo(
        home.Home,
        System.IO.Path.Combine(home.Home, "config.toml"),
        "26.814.5167.0",
        version,
        running,
        running ? ["codex (PID 1)"] : [],
        ProviderKind.OpenAI,
        "openai",
        null,
        null,
        false,
        Directory.Exists(System.IO.Path.Combine(home.Home, "backup-deepseek")),
        FileFingerprint.Missing,
        null));
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

internal sealed class FakeLmStudioSwitchPreflight : ILmStudioSwitchPreflight
{
    private readonly Queue<CodexInstructionHierarchyProbeResult> queued = new();

    public int CallCount { get; private set; }

    public CodexInstructionHierarchyProbeResult DefaultResult { get; set; } = Pass();

    public void Enqueue(CodexInstructionHierarchyProbeResult result) => queued.Enqueue(result);

    public Task<CodexInstructionHierarchyProbeResult> ProbeAsync(SwitchRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(queued.Count > 0 ? queued.Dequeue() : DefaultResult);
    }

    public static CodexInstructionHierarchyProbeResult Pass() => new(
        true,
        true,
        200,
        200,
        null,
        "test pass",
        DateTimeOffset.Now);

    public static CodexInstructionHierarchyProbeResult Fail(string code = CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder) => new(
        true,
        false,
        200,
        500,
        code,
        "test blocked",
        DateTimeOffset.Now);
}

internal sealed class SwitchHarness : IDisposable
{
    private readonly TemporaryDirectory root = new();

    public SwitchHarness(string configText, string cliVersion = "codex-cli 0.148.0")
    {
        Home = new TestCodexHomeProvider(System.IO.Path.Combine(root.Path, "home"));
        File.WriteAllText(System.IO.Path.Combine(Home.Home, "config.toml"), configText, new UTF8Encoding(false));
        AppPaths = new AppPaths(System.IO.Path.Combine(root.Path, "local"));
        Patch = new TomlConfigPatchEngine();
        Writer = new AtomicBatchWriter();
        Runtime = new FakeRuntimeProbe(Home, false, cliVersion);
        Settings = new AppSettingsRepository(AppPaths);
        Secrets = new FakeSecretStore();
        Secrets.Save(CredentialNames.DeepSeek, "sk-test".AsSpan());
        Secrets.Save(CredentialNames.LmStudio, "lm-test".AsSpan());
        Scanner = new SecondaryModelOverrideScanner(Patch);
        Backups = new BackupService(Home, Writer, Patch, "1.0.0-test", cliVersion);
        Preflight = new FakeLmStudioSwitchPreflight();
        Service = new ConfigurationSwitchService(Home, Patch, Writer, Backups, Scanner, Runtime, Settings, Secrets, Preflight);
        HelperPath = System.IO.Path.Combine(root.Path, "CredentialHelper.exe");
        File.WriteAllText(HelperPath, "test");
        CatalogPath = System.IO.Path.Combine(root.Path, "deepseek-models.json");
        File.WriteAllText(CatalogPath, TestCatalog, new UTF8Encoding(false));
    }

    public TestCodexHomeProvider Home { get; }
    public AppPaths AppPaths { get; }
    public TomlConfigPatchEngine Patch { get; }
    public AtomicBatchWriter Writer { get; }
    public FakeRuntimeProbe Runtime { get; }
    public AppSettingsRepository Settings { get; }
    public FakeSecretStore Secrets { get; }
    public SecondaryModelOverrideScanner Scanner { get; }
    public BackupService Backups { get; }
    public FakeLmStudioSwitchPreflight Preflight { get; }
    public ConfigurationSwitchService Service { get; }
    public string HelperPath { get; }
    public string CatalogPath { get; }

    public SwitchRequest Request(ProviderKind provider) => provider switch
    {
        ProviderKind.OpenAI => new SwitchRequest(provider, "gpt-5.6-sol", "max"),
        ProviderKind.DeepSeek => new SwitchRequest(provider, "deepseek-v4-pro", "high", CredentialHelperPath: HelperPath, DeepSeekCatalogPath: CatalogPath),
        ProviderKind.LmStudio => new SwitchRequest(provider, "qwen/local@q6", null, 65_536, ConfigurationSwitchService.SuggestAutoCompact(65_536), LmStudioProviderId: "lmstudio", LmStudioEndpoint: new Uri("http://127.0.0.1:1234"), CredentialHelperPath: HelperPath),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public string ReadConfig() => File.ReadAllText(System.IO.Path.Combine(Home.Home, "config.toml"));

    public void Dispose() => root.Dispose();

    public const string BaseConfig = """
        # user comment must survive
        model = "gpt-old" # inline
        model_reasoning_effort = "max"
        review_model = "gpt-review"

        [mcp_servers.demo]
        command = "demo.exe"

        [projects.'C:\work']
        trust_level = "trusted"

        [permissions.safe]
        network = false

        [hooks]
        enabled = true

        [plugins]
        enabled = true
        """;

    private const string TestCatalog = """
        {
          "models": [
            {
              "slug": "deepseek-v4-pro",
              "display_name": "DeepSeek-V4-Pro",
              "description": "official-test-shape",
              "context_window": 1048576,
              "minimal_client_version": "0.144.0",
              "default_reasoning_level": "high",
              "supported_reasoning_levels": [
                { "effort": "low", "description": "low" },
                { "effort": "high", "description": "high" },
                { "effort": "max", "description": "max" }
              ]
            }
          ]
        }
        """;
}
