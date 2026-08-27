using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class TemplateStorageRemediationTests
{
    [Fact]
    public async Task NullRuleVersionIsRejectedAsInvalidJournalInsteadOfNullReference()
    {
        using var temporary = new TemporaryDirectory();
        var store = new LmStudioTemplateTransactionStore(temporary.Path);
        Guid transactionId = Guid.NewGuid();
        await store.WriteAsync(CreateRecord(transactionId, temporary.Path));
        string path = store.GetPath(transactionId);
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["ruleVersion"] = null;
        await File.WriteAllTextAsync(path, json.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(transactionId));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAllAsync());
    }

    [Fact]
    public async Task UnrelatedJsonIsIgnoredButCorruptGuidJournalStillBlocks()
    {
        using var temporary = new TemporaryDirectory();
        var store = new LmStudioTemplateTransactionStore(temporary.Path);
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "notes.json"), "{not-a-journal");

        Assert.Empty(await store.ListAllAsync());

        string corruptJournal = Path.Combine(temporary.Path, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(corruptJournal, "{broken");
        await Assert.ThrowsAsync<JsonException>(() => store.ListAllAsync());
    }

    [Fact]
    public async Task OnlyExactStaleTransactionTemporaryFilesAreCleaned()
    {
        using var temporary = new TemporaryDirectory();
        var store = new LmStudioTemplateTransactionStore(temporary.Path);
        string stale = Path.Combine(
            temporary.Path,
            Guid.NewGuid().ToString("N") + ".json.tmp-" + Guid.NewGuid().ToString("N"));
        string fresh = Path.Combine(
            temporary.Path,
            Guid.NewGuid().ToString("N") + ".json.tmp-" + Guid.NewGuid().ToString("N"));
        string unknown = Path.Combine(temporary.Path, "notes.json.tmp-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(stale, "stale");
        await File.WriteAllTextAsync(fresh, "fresh");
        await File.WriteAllTextAsync(unknown, "unknown");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.Subtract(TimeSpan.FromHours(25)));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.Subtract(TimeSpan.FromHours(1)));
        File.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.Subtract(TimeSpan.FromDays(2)));

        Assert.Empty(await store.ListAllAsync());

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unknown));
        Assert.True(LmStudioTemplateTransactionStore.IsTransactionTemporaryFileName(Path.GetFileName(fresh)));
        Assert.False(LmStudioTemplateTransactionStore.IsTransactionTemporaryFileName(Path.GetFileName(unknown)));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com9.gguf", "_Com9.gguf")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("model", "model")]
    public void ExportPathSegmentProtectsWindowsDeviceNames(string input, string expected)
    {
        Assert.Equal(expected, PromptTemplateRepairService.SanitizePathSegment(input));
    }

    [Fact]
    public void StrictDescendantCheckHandlesTrailingSeparatorsAndSiblingPrefixes()
    {
        using var temporary = new TemporaryDirectory();
        string rootWithSeparator = temporary.Path + Path.DirectorySeparatorChar;
        string child = Path.Combine(temporary.Path, "child", "export");
        string sibling = temporary.Path + "-sibling";

        Assert.True(PromptTemplateRepairService.IsStrictDescendant(rootWithSeparator, child));
        Assert.False(PromptTemplateRepairService.IsStrictDescendant(rootWithSeparator, temporary.Path));
        Assert.False(PromptTemplateRepairService.IsStrictDescendant(rootWithSeparator, sibling));
    }

    [Fact]
    public void StrictDescendantCheckPreservesVolumeRootSemantics()
    {
        using var temporary = new TemporaryDirectory();
        string volumeRoot = Path.GetPathRoot(temporary.Path)!;

        Assert.True(PromptTemplateRepairService.IsStrictDescendant(volumeRoot, temporary.Path));
        Assert.False(PromptTemplateRepairService.IsStrictDescendant(volumeRoot, volumeRoot));
    }

    [Fact]
    public async Task ConcurrentExportsAlwaysUseDistinctCompleteDirectories()
    {
        using var temporary = new TemporaryDirectory();
        GgufChatTemplateAnalysis analysis = CreateSupportedAnalysis(temporary.Path);
        var service = new PromptTemplateRepairService(new FixedTemplateReader(analysis));
        string output = Path.Combine(temporary.Path, "exports");

        PromptTemplateRepairArtifact[] artifacts = await Task.WhenAll(
            service.ExportAsync(analysis, "qwen/model", output),
            service.ExportAsync(analysis, "qwen/model", output));

        Assert.NotEqual(artifacts[0].Directory, artifacts[1].Directory);
        foreach (PromptTemplateRepairArtifact artifact in artifacts)
        {
            Assert.True(Directory.Exists(artifact.Directory));
            Assert.Equal(4, Directory.EnumerateFiles(artifact.Directory).Count());
            Assert.True(File.Exists(artifact.ManifestPath));
        }
    }

    [Fact]
    public async Task FailedExportCleansStrictChildWhenOutputRootHasTrailingSeparator()
    {
        using var temporary = new TemporaryDirectory();
        GgufChatTemplateAnalysis analysis = CreateSupportedAnalysis(temporary.Path);
        using var cancellation = new CancellationTokenSource();
        var service = new PromptTemplateRepairService(new CancellingTemplateReader(analysis, cancellation));
        string output = Path.Combine(temporary.Path, "exports") + Path.DirectorySeparatorChar;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportAsync(analysis, "qwen-model", output, cancellation.Token));

        string modelDirectory = Path.Combine(output, "qwen-model");
        Assert.True(Directory.Exists(modelDirectory));
        Assert.Empty(Directory.EnumerateDirectories(modelDirectory));
    }

    [Fact]
    public async Task RelativeDownloadsFolderMapsToInvalidSettingsInBothLocatorPaths()
    {
        using var temporary = new TemporaryDirectory();
        string settingsDirectory = Path.Combine(temporary.Path, ".lmstudio");
        Directory.CreateDirectory(settingsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectory, "settings.json"),
            "{\"downloadsFolder\":\"models\"}");
        var runner = new CountingLmsRunner();
        var locator = new LmStudioModelFileLocator(runner, () => temporary.Path);
        ModelProfile model = CreateLoadedModel();

        LmStudioModelFileResolutionAttempt asyncAttempt = await locator.ResolveAsync(
            model,
            new Uri("http://127.0.0.1:1234"));
        LmStudioModelFileResolutionAttempt psAttempt = LmStudioModelFileLocator.ResolvePsFromJson(
            model,
            "[]",
            "{\"downloadsFolder\":\"models\"}",
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.InvalidSettings, asyncAttempt.Status);
        Assert.Equal(LmStudioModelFileResolutionStatus.InvalidSettings, psAttempt.Status);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void LmsPsNumericFieldsRejectWrongKindsWithoutInvalidOperationEscape()
    {
        using var temporary = new TemporaryDirectory();
        const string processesJson = """
            [
              {
                "modelKey": "qwen/model:1",
                "identifier": "qwen/model:1",
                "publisher": "qwen",
                "type": "llm",
                "architecture": "qwen35",
                "quantization": "Q8_0",
                "contextLength": "not-a-number",
                "format": "gguf"
              }
            ]
            """;

        LmStudioModelFileResolutionAttempt attempt = LmStudioModelFileLocator.ResolvePsFromJson(
            CreateLoadedModel(),
            processesJson,
            null,
            temporary.Path);

        Assert.Equal(LmStudioModelFileResolutionStatus.IdentityMismatch, attempt.Status);
    }

    private static LmStudioTemplateTransactionRecord CreateRecord(Guid transactionId, string root)
    {
        var snapshot = new LmStudioLoadedInstanceSnapshot(
            new Uri("http://127.0.0.1:1234"),
            "qwen/model",
            "qwen/model:1",
            null,
            "qwen35",
            "Q8_0",
            "7B",
            "llm",
            32_768,
            new LmStudioLoadConfiguration(ContextLength: 16_384),
            null,
            false,
            DateTimeOffset.UtcNow,
            "snapshot");
        return new LmStudioTemplateTransactionRecord(
            1,
            transactionId,
            LmStudioTemplateTransactionState.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            snapshot,
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
            Path.GetFullPath(Path.Combine(root, "model.gguf")),
            "model.gguf",
            1,
            DateTimeOffset.UtcNow,
            3,
            new string('A', 64),
            new string('B', 64),
            PromptTemplateRepairService.CurrentRuleVersion,
            "qwen/model:2",
            "test");
    }

    private static GgufChatTemplateAnalysis CreateSupportedAnalysis(string root)
    {
        string template = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "unsloth-qwen3.8-prefix-template.jinja"));
        return new GgufChatTemplateAnalysis(
            Path.Combine(root, "model.gguf"),
            "model.gguf",
            123,
            DateTimeOffset.UnixEpoch,
            3,
            "Fixture",
            "qwen35",
            template,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template))));
    }

    private static ModelProfile CreateLoadedModel() => new(
        "qwen/model:1",
        "Qwen",
        ProviderKind.LmStudio,
        Quantization: "Q8_0",
        IsLoaded: true,
        LoadedContextLength: 16_384,
        LoadedInstanceId: "qwen/model:1",
        Architecture: "qwen35",
        ModelType: "llm",
        SourceModelKey: "qwen/model",
        Format: "gguf");

    private sealed class FixedTemplateReader(GgufChatTemplateAnalysis analysis) : IGgufChatTemplateReader
    {
        public Task<GgufChatTemplateAnalysis> ReadAsync(
            string filePath,
            CancellationToken cancellationToken = default) => Task.FromResult(analysis);
    }

    private sealed class CancellingTemplateReader(
        GgufChatTemplateAnalysis analysis,
        CancellationTokenSource cancellation) : IGgufChatTemplateReader
    {
        public Task<GgufChatTemplateAnalysis> ReadAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(analysis);
        }
    }

    private sealed class CountingLmsRunner : ILmsCliCommandRunner
    {
        public int CallCount { get; private set; }

        public Task<LmsCliCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new LmsCliCommandResult(LmsCliCommandStatus.Failed, null));
        }
    }
}
