using CodexModelManager.App.UI;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.App.Tests;

public sealed class SelectionAuditTests
{
    [Fact]
    public Task ClosingHasABoundedWaitForAnUncooperativeAction() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = fixture.Controller.RunUiActionForTestAsync(() => release.Task);
        await fixture.Controller.PrepareForCloseAsync(TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(running.IsCompleted);
        Assert.False(fixture.Form.Visible);
        release.SetResult();
        await running;
    });

    [Fact]
    public Task GlmPlatformRowIsHiddenUnlessProviderIsGlm() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Form.Current.GlmPlatformRowVisible);
        Assert.False(fixture.Form.Current.GlmPlatformCombo.Visible);

        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.GLM;
        Assert.True(fixture.Form.Current.GlmPlatformRowVisible);

        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.OpenAI;
        Assert.False(fixture.Form.Current.GlmPlatformRowVisible);

        await fixture.Controller.RunUiActionForTestAsync(() => Task.CompletedTask);
    });

    [Fact]
    public Task ConfiguredSecondLoadedInstanceSynchronizesBothPages() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Form.Current.CurrentModelValue.Text = "loaded-B";
        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.LmStudio;
        await fixture.Controller.LoadModelsForSelectedProviderAsync();
        Assert.Equal("loaded-B", ((ModelProfile)fixture.Form.Current.ModelCombo.SelectedItem!).Id);
        Assert.Equal("loaded-B", ((ModelProfile)fixture.Form.LmStudio.ModelCombo.SelectedItem!).Id);
        Assert.Equal(32_768m, fixture.Form.LmStudio.CodexContextInput.Value);
        Assert.False(fixture.Form.Visible);
    });

    [Fact]
    public Task ProviderChangeDuringActionIsCoalescedInsteadOfLost() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Controller.LoadModelsForSelectedProviderAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = fixture.Controller.RunUiActionForTestAsync(() => release.Task);
        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.LmStudio;
        release.SetResult();
        await running;
        Assert.Equal(ProviderKind.LmStudio, ((ModelProfile)fixture.Form.Current.ModelCombo.SelectedItem!).Provider);
        Assert.Equal(((ModelProfile)fixture.Form.Current.ModelCombo.SelectedItem!).Id, ((ModelProfile)fixture.Form.LmStudio.ModelCombo.SelectedItem!).Id);
    });

    [Fact]
    public Task ObsoleteCatalogCannotOverwriteLatestSelection() => StaTest.RunAsync(async () =>
    {
        var oldCatalog = new TaskCompletionSource<IReadOnlyList<ModelProfile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((provider, _) => provider == ProviderKind.OpenAI ? oldCatalog.Task : Task.FromResult(LocalModels));
        Task running = fixture.Controller.RunUiActionForTestAsync(fixture.Controller.LoadModelsForSelectedProviderAsync);
        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.LmStudio;
        oldCatalog.SetResult([new ModelProfile("old-openai", "Old", ProviderKind.OpenAI)]);
        await running;
        Assert.Equal(ProviderKind.LmStudio, ((ModelProfile)fixture.Form.Current.ModelCombo.SelectedItem!).Provider);
        Assert.DoesNotContain(fixture.Form.Current.ModelCombo.Items.Cast<ModelProfile>(), model => model.Id == "old-openai");
    });

    [Fact]
    public Task TransactionInputsRemainFrozenAndRestoreAfterCompletion() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = fixture.Controller.RunUiActionForTestAsync(() => release.Task, freezeInputs: true);
        Assert.False(fixture.Form.Current.ProviderCombo.Enabled);
        Assert.False(fixture.Form.LmStudio.EndpointText.Enabled);
        fixture.Form.Current.ProviderCombo.Enabled = true;
        Assert.False(fixture.Form.Current.ProviderCombo.Enabled);
        release.SetResult();
        await running;
        Assert.True(fixture.Form.Current.ProviderCombo.Enabled);
        Assert.True(fixture.Form.LmStudio.EndpointText.Enabled);
    });

    [Fact]
    public Task SameInstanceCatalogRefreshPreservesTheResolvedGgufPath() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.LmStudio;
        await fixture.Controller.LoadModelsForSelectedProviderAsync();
        const string resolvedPath = @"C:\isolated-fixture\loaded-A.gguf";
        fixture.Form.LmStudio.GgufPathText.Text = resolvedPath;

        await fixture.Controller.LoadModelsForSelectedProviderAsync();

        Assert.Equal(resolvedPath, fixture.Form.LmStudio.GgufPathText.Text);
        Assert.True(fixture.Form.LmStudio.AnalyzeTemplateButton.Enabled);
        Assert.Equal("loaded-A", ((ModelProfile)fixture.Form.LmStudio.ModelCombo.SelectedItem!).Id);
        Assert.False(fixture.Form.Visible);
    });

    [Fact]
    public Task RecheckClickFreezesSelectionUntilItsCanceledRequestHasFinished() => StaTest.RunAsync(async () =>
    {
        using var handler = new BlockingHttpHandler();
        using var fixture = new Fixture(handler: handler);
        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.LmStudio;
        await fixture.Controller.LoadModelsForSelectedProviderAsync();
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(fixture.Form.LmStudio.RecheckHierarchyButton, [EventArgs.Empty]);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(fixture.Form.Current.ModelCombo.Enabled);
        Assert.False(fixture.Form.LmStudio.ModelCombo.Enabled);
        Assert.False(fixture.Form.LmStudio.EndpointText.Enabled);
        Assert.False(fixture.Form.LmStudio.GgufPathText.Enabled);
        await fixture.Controller.PrepareForCloseAsync();
        Assert.True(handler.Canceled);
        Assert.False(fixture.Form.Visible);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ReloadedSecondInstanceRestoresOnlyStillSupportedConfirmedReasoning(bool remainsSupported) => StaTest.RunAsync(async () =>
    {
        IReadOnlyList<ModelProfile> currentModels =
        [
            LocalModels[0],
            LocalModels[1] with { ReasoningOptions = ["high"], SupportsReasoning = true },
        ];
        using var fixture = new Fixture((_, _) => Task.FromResult(currentModels));
        fixture.Form.Current.ProviderCombo.SelectedItem = ProviderKind.LmStudio;
        await fixture.Controller.LoadModelsForSelectedProviderAsync();
        fixture.Form.Current.ModelCombo.SelectedItem = currentModels[1];
        fixture.Form.Current.ReasoningCombo.SelectedItem = "high";
        string confirmedReasoning = Assert.IsType<string>(fixture.Form.Current.ReasoningCombo.SelectedItem);
        currentModels =
        [
            LocalModels[0],
            LocalModels[1] with { Id = "reloaded-B", LoadedInstanceId = "reloaded-B", ReasoningOptions = remainsSupported ? ["high"] : [] },
        ];
        await fixture.Controller.LoadModelsForSelectedProviderAsync();
        Assert.Equal("loaded-A", ((ModelProfile)fixture.Form.Current.ModelCombo.SelectedItem!).Id);

        fixture.Controller.SelectReloadedLmStudioInstance("reloaded-B", null, AutoCompactMode.Automatic, confirmedReasoning);

        Assert.Equal("reloaded-B", ((ModelProfile)fixture.Form.Current.ModelCombo.SelectedItem!).Id);
        Assert.Equal(remainsSupported ? "high" : "（不写入）", fixture.Form.Current.ReasoningCombo.SelectedItem);
    });

    [Fact]
    public Task RollbackUsesAnIndependentBudgetEvenAfterUiCloseAndLoggingFailure() => StaTest.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Controller.PrepareForCloseAsync();
        int result = await MainController.RunIndependentRollbackAsync(
            async token =>
            {
                Assert.True(token.CanBeCanceled);
                Assert.False(token.IsCancellationRequested);
                await Task.Delay(10, token);
                return 42;
            },
            () => throw new IOException("injected logging failure"));
        Assert.Equal(42, result);
    });

    [Fact]
    public void UncertainCommitIsMarkedSafeBeforeAnyFallibleNotification()
    {
        bool committed = false;
        bool observedSafeState = false;
        MainController.PreserveCommittedState(ref committed, () =>
        {
            observedSafeState = committed;
            throw new IOException("injected recovery notification failure");
        });
        Assert.True(committed);
        Assert.True(observedSafeState);
    }

    [Fact]
    public void RepairConfirmationOnlyAllowsReturnedInstanceIdToChange()
    {
        var confirmed = new SwitchRequest(ProviderKind.LmStudio, "old", ContextWindow: 32_768, LmStudioEndpoint: new Uri("http://127.0.0.1:1234"));
        Assert.True(MainController.RepairRequestMatchesConfirmation(confirmed, confirmed with { TargetModel = "reloaded" }));
        Assert.False(MainController.RepairRequestMatchesConfirmation(confirmed, confirmed with { TargetModel = "reloaded", ContextWindow = 8_192 }));
        Assert.False(MainController.RepairRequestMatchesConfirmation(confirmed, confirmed with { LmStudioEndpoint = new Uri("http://127.0.0.1:1235") }));
    }

    private static readonly IReadOnlyList<ModelProfile> LocalModels =
    [
        new("loaded-A", "A", ProviderKind.LmStudio, IsLoaded: true, LoadedContextLength: 8_192, ModelType: "llm", LoadedInstanceId: "loaded-A"),
        new("loaded-B", "B", ProviderKind.LmStudio, IsLoaded: true, LoadedContextLength: 32_768, ModelType: "llm", LoadedInstanceId: "loaded-B"),
    ];

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CodexModelManager.Tests", Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;
        private readonly AppComposition composition;
        public MainForm Form { get; } = new();
        public MainController Controller { get; }

        public Fixture(Func<ProviderKind, CancellationToken, Task<IReadOnlyList<ModelProfile>>>? discover = null, HttpMessageHandler? handler = null)
        {
            var logger = new QuietLogger();
            http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            composition = new AppComposition(new AppPaths(root), new EmptySecrets(), logger);
            Controller = new MainController(Form, composition, logger, http, discover ?? ((provider, _) => Task.FromResult(provider == ProviderKind.LmStudio ? LocalModels : new ModelProfile[] { new("gpt-fixture", "Fixture", ProviderKind.OpenAI) })));
        }

        public void Dispose()
        {
            Controller.Dispose();
            Form.Dispose();
            composition.Dispose();
            http.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EmptySecrets : ISecretStore
    {
        public string? Read(string targetName) => null;
        public bool Exists(string targetName) => false;
        public void Save(string targetName, ReadOnlySpan<char> secret) => throw new InvalidOperationException("Unexpected credential write.");
        public void Delete(string targetName) => throw new InvalidOperationException("Unexpected credential delete.");
    }

    private sealed class QuietLogger : IAppLogger
    {
        public event EventHandler<string>? MessageLogged { add { } remove { } }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void LogError(string message, Exception? exception = null) { }
    }

    private sealed class BlockingHttpHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
