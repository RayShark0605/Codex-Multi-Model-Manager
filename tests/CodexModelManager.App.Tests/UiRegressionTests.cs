using System.ComponentModel;
using CodexModelManager.App.UI;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Security;

namespace CodexModelManager.App.Tests;

public sealed class UiRegressionTests
{
    [Fact]
    public void ProviderAndLmStudioLifecycleTimeoutBudgetsRemainSeparated()
    {
        using var temporary = new TemporaryDirectory();
        using var composition = new AppComposition(new AppPaths(temporary.Path), new ThrowingSecretStore(), new RecordingLogger());
        using var controller = composition.CreateLmStudioInstanceController(new Uri("http://127.0.0.1:1234"), requiresAuthentication: false);

        Assert.Equal(TimeSpan.FromMinutes(3), AppComposition.ProviderRequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), AppComposition.LmStudioLifecycleRequestTimeout);
        Assert.Equal(AppComposition.ProviderRequestTimeout, composition.ProviderHttpClientTimeout);
        Assert.Equal(AppComposition.LmStudioLifecycleRequestTimeout, composition.LmStudioLifecycleHttpClientTimeout);
        Assert.Equal(AppComposition.LmStudioLifecycleRequestTimeout, controller.RequestTimeout);
    }

    [Fact]
    public Task MainOperationPanelsDockAboveTheirTables() => StaTest.RunAsync(() =>
    {
        using var current = new CurrentSwitchControl { Size = new Size(1_000, 800) };
        using var lmStudio = new LmStudioControl { Size = new Size(1_000, 1_200) };
        AssertPanelAboveTable(current);
        AssertPanelAboveTable(lmStudio);
    });

    [Fact]
    public Task EightMillionContextIsNotClamped() => StaTest.RunAsync(() =>
    {
        using var control = new LmStudioControl();
        control.CodexContextInput.Value = 8_000_000;
        control.AutoCompactInput.Value = 8_000_000;
        Assert.Equal(8_000_000m, control.CodexContextInput.Value);
        Assert.Equal(8_000_000m, control.AutoCompactInput.Value);
        Assert.Equal(int.MaxValue, control.CodexContextInput.Maximum);
    });

    [Fact]
    public Task PromptTemplateAnalyzeButtonTracksLoadedLlmAndGgufPath() => StaTest.RunAsync(() =>
    {
        using var temporary = new TemporaryDirectory();
        var logger = new RecordingLogger();
        using var composition = new AppComposition(new AppPaths(temporary.Path), new ThrowingSecretStore(), logger);
        using var form = new MainForm();
        using var httpClient = new HttpClient();
        using var controller = new MainController(form, composition, logger, httpClient);
        ModelProfile loadedModel = CreateLoadedLmStudioModel();

        Assert.False(form.LmStudio.AnalyzeTemplateButton.Enabled);
        form.LmStudio.ModelCombo.Items.Add(loadedModel);
        form.LmStudio.ModelCombo.SelectedItem = loadedModel;
        Assert.False(form.LmStudio.AnalyzeTemplateButton.Enabled);

        form.LmStudio.GgufPathText.Text = @"J:\LM Studio Models\esatapedico\model.gguf";
        Assert.True(form.LmStudio.AnalyzeTemplateButton.Enabled);

        form.LmStudio.GgufPathText.Clear();
        Assert.False(form.LmStudio.AnalyzeTemplateButton.Enabled);
        form.LmStudio.GgufPathText.Text = @"J:\LM Studio Models\esatapedico\model.gguf";
        form.LmStudio.ModelCombo.SelectedItem = null;
        Assert.False(form.LmStudio.AnalyzeTemplateButton.Enabled);
    });

    [Fact]
    public Task PersistenceStatusLabelsColorsAndReloadDriftClassificationAreStable() => StaTest.RunAsync(() =>
    {
        using var control = new LmStudioControl();
        Assert.Contains("Persistence State Ambiguous", control.PersistenceStatusValue.Text, StringComparison.Ordinal);
        Assert.Equal(Color.DarkOrange, control.PersistenceStatusValue.ForeColor);
        Assert.Equal("Built-in / No Override", MainController.PersistenceStatusName(LmStudioPersistenceStatus.BuiltInNoOverride));
        Assert.Equal("Legacy Runtime-Only Patch", MainController.PersistenceStatusName(LmStudioPersistenceStatus.LegacyRuntimeOnlyPatch));
        Assert.Equal("Persistent v3 Applied", MainController.PersistenceStatusName(LmStudioPersistenceStatus.PersistentV3Applied));
        Assert.Equal("Persistent v2 Upgrade Required", MainController.PersistenceStatusName(LmStudioPersistenceStatus.PersistentV2UpgradeRequired));
        Assert.Equal("Persistent Override Missing After Reload", MainController.PersistenceStatusName(LmStudioPersistenceStatus.PersistentOverrideMissingAfterReload));
        Assert.Equal("Unsupported Custom Override", MainController.PersistenceStatusName(LmStudioPersistenceStatus.UnsupportedCustomOverride));
        Assert.Equal("Unsupported LM Studio Version", MainController.PersistenceStatusName(LmStudioPersistenceStatus.UnsupportedLmStudioVersion));
        Assert.Equal("Persistence State Ambiguous", MainController.PersistenceStatusName(LmStudioPersistenceStatus.PersistenceStateAmbiguous));

        Assert.Equal(Color.DarkGreen, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.PersistentV3Applied));
        Assert.Equal(Color.DarkOrange, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.BuiltInNoOverride));
        Assert.Equal(Color.DarkOrange, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.LegacyRuntimeOnlyPatch));
        Assert.Equal(Color.DarkOrange, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.PersistentV2UpgradeRequired));
        Assert.Equal(Color.DarkOrange, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.PersistentOverrideMissingAfterReload));
        Assert.Equal(Color.Firebrick, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.UnsupportedCustomOverride));
        Assert.Equal(Color.Firebrick, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.UnsupportedLmStudioVersion));
        Assert.Equal(Color.Firebrick, MainController.PersistenceStatusColor(LmStudioPersistenceStatus.PersistenceStateAmbiguous));

        Assert.Equal(
            LmStudioPersistenceStatus.BuiltInNoOverride,
            MainController.ClassifyMissingPersistentOverride(hasLegacyCompleted: false, hierarchyCompatible: null));
        Assert.Equal(
            LmStudioPersistenceStatus.LegacyRuntimeOnlyPatch,
            MainController.ClassifyMissingPersistentOverride(hasLegacyCompleted: true, hierarchyCompatible: true));
        Assert.Equal(
            LmStudioPersistenceStatus.PersistentOverrideMissingAfterReload,
            MainController.ClassifyMissingPersistentOverride(hasLegacyCompleted: true, hierarchyCompatible: false));
    });

    [Fact]
    public void EmptyPromptTemplatePathProducesActionableChineseValidationError()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            MainController.ValidatePromptTemplateAnalysisInput(CreateLoadedLmStudioModel(), "  "));

        Assert.Contains("尚未定位对应 GGUF", exception.Message, StringComparison.Ordinal);
        Assert.Contains("选择 GGUF", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("filePath", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public Task UiLogIsTrimmedOnWholeLineAndRemainsBounded() => StaTest.RunAsync(() =>
    {
        using var log = new TextBox { Multiline = true, MaxLength = MainController.MaximumUiLogCharacters };
        string line = new string('a', 98) + Environment.NewLine;
        log.Text = string.Concat(Enumerable.Repeat(line, 9_000));

        MainController.AppendLogMessage(log, new string('b', 200_000));

        Assert.InRange(log.TextLength, 1, MainController.MaximumUiLogCharacters);
        Assert.True(log.Text.StartsWith(line, StringComparison.Ordinal) || log.Text.StartsWith('b'));
        Assert.EndsWith(Environment.NewLine, log.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void UiExceptionReporterLogsAndRedactsBeforeShowing()
    {
        var logger = new RecordingLogger();
        var redactor = new SecretRedactor();
        redactor.Register("sk-test-secret");
        string? shown = null;
        var reporter = new UiExceptionReporter(logger, redactor, (message, _, _) => shown = message);

        reporter.Report(new Win32Exception("CredWrite failed for sk-test-secret"), "test");

        Assert.Single(logger.Errors);
        Assert.NotNull(shown);
        Assert.Contains(nameof(Win32Exception), shown, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-secret", shown, StringComparison.Ordinal);
        Assert.Contains("<redacted>", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramThreadExceptionHandlerUsesTheSafeReporter()
    {
        var logger = new RecordingLogger();
        var redactor = new SecretRedactor();
        redactor.Register("sk-thread-secret");
        string? shown = null;
        var reporter = new UiExceptionReporter(logger, redactor, (message, _, _) => shown = message);
        ThreadExceptionEventHandler handler = Program.CreateThreadExceptionHandler(reporter);

        handler(new object(), new ThreadExceptionEventArgs(new Win32Exception("failure sk-thread-secret")));

        Assert.Single(logger.Errors);
        Assert.NotNull(shown);
        Assert.DoesNotContain("sk-thread-secret", shown, StringComparison.Ordinal);
        Assert.Contains("<redacted>", shown, StringComparison.Ordinal);
    }

    [Fact]
    public Task CredentialWriteFailureUsesUnifiedUiActionErrorPath() => StaTest.RunAsync(async () =>
    {
        using var temporary = new TemporaryDirectory();
        var logger = new RecordingLogger();
        using var composition = new AppComposition(new AppPaths(temporary.Path), new ThrowingSecretStore(), logger);
        using var form = new MainForm();
        using var httpClient = new HttpClient();
        using var controller = new MainController(form, composition, logger, httpClient);
        using var input = new TextBox { Text = "sk-test-secret" };

        await controller.RunUiActionForTestAsync(() => controller.SaveCredentialAsync("test-target", input));

        Assert.Single(logger.Errors);
        Assert.IsType<Win32Exception>(logger.Errors[0].Exception);
        Assert.Equal("sk-test-secret", input.Text);
    });

    [Fact]
    public Task CredentialStatusRefreshFailureUsesUnifiedUiActionErrorPath() => StaTest.RunAsync(async () =>
    {
        using var temporary = new TemporaryDirectory();
        var logger = new RecordingLogger();
        using var composition = new AppComposition(new AppPaths(temporary.Path), new RefreshThrowingSecretStore(), logger);
        using var form = new MainForm();
        using var httpClient = new HttpClient();
        using var controller = new MainController(form, composition, logger, httpClient);
        using var input = new TextBox { Text = "sk-test-secret" };

        await controller.RunUiActionForTestAsync(() => controller.SaveCredentialAsync("test-target", input));

        Assert.Single(logger.Errors);
        Assert.IsType<Win32Exception>(logger.Errors[0].Exception);
        Assert.Empty(input.Text);
    });

    [Fact]
    public Task ClosePreparationWaitsForActiveActionAndRejectsNewActions() => StaTest.RunAsync(async () =>
    {
        using var temporary = new TemporaryDirectory();
        var logger = new RecordingLogger();
        using var composition = new AppComposition(new AppPaths(temporary.Path), new ThrowingSecretStore(), logger);
        using var form = new MainForm();
        using var httpClient = new HttpClient();
        using var controller = new MainController(form, composition, logger, httpClient);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int laterActions = 0;
        Task active = controller.RunUiActionForTestAsync(async () =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task;

        Task closing = controller.PrepareForCloseAsync();
        Assert.False(closing.IsCompleted);
        release.SetResult();
        await Task.WhenAll(active, closing);
        await controller.RunUiActionForTestAsync(() =>
        {
            laterActions++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, laterActions);
    });

    [Fact]
    public async Task PlannerOwnershipHelperUnsubscribesAndDisposesOnFailure()
    {
        var resource = new EventResource();
        int progressEvents = 0;
        EventHandler handler = (_, _) => progressEvents++;
        resource.Progress += handler;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MainController.TransferOwnershipOnSuccessAsync<EventResource, int>(
                resource,
                _ => throw new InvalidDataException("injected planning failure"),
                owned =>
                {
                    owned.Progress -= handler;
                    owned.Dispose();
                }));
        resource.RaiseProgress();

        Assert.True(resource.IsDisposed);
        Assert.Equal(0, progressEvents);
    }

    [Fact]
    public Task LogCallbackIsSafeBeforeHandleCreationAcrossThreadsAndAfterDisposal() => StaTest.RunAsync(async () =>
    {
        using var temporary = new TemporaryDirectory();
        var logger = new RecordingLogger();
        using var composition = new AppComposition(new AppPaths(temporary.Path), new ThrowingSecretStore(), logger);
        using var form = new MainForm();
        using var httpClient = new HttpClient();
        using var controller = new MainController(form, composition, logger, httpClient);

        await Task.Run(() => logger.Info("before-handle"));
        Assert.Empty(form.SettingsLog.Log.Text);

        _ = form.Handle;
        _ = form.SettingsLog.Handle;
        _ = form.SettingsLog.Log.Handle;
        await Task.Run(() => logger.Info("cross-thread"));
        for (int attempt = 0; attempt < 50 && !form.SettingsLog.Log.Text.Contains("cross-thread", StringComparison.Ordinal); attempt++)
        {
            Application.DoEvents();
            await Task.Delay(10);
        }

        Assert.Contains("cross-thread", form.SettingsLog.Log.Text, StringComparison.Ordinal);
        form.Dispose();
        await Task.Run(() => logger.Info("after-dispose"));
    });

    private static void AssertPanelAboveTable(UserControl control)
    {
        control.CreateControl();
        control.PerformLayout();
        FlowLayoutPanel buttons = Assert.Single(control.Controls.OfType<FlowLayoutPanel>());
        TableLayoutPanel table = Assert.Single(control.Controls.OfType<TableLayoutPanel>());
        Assert.True(buttons.Top <= table.Top, $"buttons.Top={buttons.Top}, table.Top={table.Top}");
        Assert.True(buttons.Bottom <= table.Top, $"buttons.Bottom={buttons.Bottom}, table.Top={table.Top}");
    }

    private static ModelProfile CreateLoadedLmStudioModel() => new(
        "qwen3.8-27b-nvfp4-mtp",
        "Qwen3.8 27B NVFP4 MTP HIGHEST",
        ProviderKind.LmStudio,
        Quantization: null,
        IsLoaded: true,
        MaxContextLength: 262_144,
        LoadedContextLength: 262_144,
        LoadedInstanceId: "qwen3.8-27b-nvfp4-mtp",
        Architecture: "qwen35",
        ModelType: "llm",
        SourceModelKey: "esatapedico/qwen3.8-27b-nvfp4-mtp-gguf/qwen3.8-27b-nvfp4-mtp-highest.gguf",
        Format: "gguf");

    private sealed class RecordingLogger : IAppLogger
    {
        public event EventHandler<string>? MessageLogged;

        public List<(string Message, Exception? Exception)> Errors { get; } = [];

        public void Info(string message) => MessageLogged?.Invoke(this, message);

        public void Warning(string message) => MessageLogged?.Invoke(this, message);

        public void LogError(string message, Exception? exception = null)
        {
            Errors.Add((message, exception));
            MessageLogged?.Invoke(this, message);
        }
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        public void Save(string targetName, ReadOnlySpan<char> secret) => throw new Win32Exception("injected credential failure");

        public string? Read(string targetName) => null;

        public bool Exists(string targetName) => false;

        public void Delete(string targetName)
        {
        }
    }

    private sealed class EventResource : IDisposable
    {
        public event EventHandler? Progress;

        public bool IsDisposed { get; private set; }

        public void RaiseProgress() => Progress?.Invoke(this, EventArgs.Empty);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RefreshThrowingSecretStore : ISecretStore
    {
        public void Save(string targetName, ReadOnlySpan<char> secret)
        {
        }

        public string? Read(string targetName) => null;

        public bool Exists(string targetName) => throw new Win32Exception("injected credential status failure");

        public void Delete(string targetName)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexModelManager.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
