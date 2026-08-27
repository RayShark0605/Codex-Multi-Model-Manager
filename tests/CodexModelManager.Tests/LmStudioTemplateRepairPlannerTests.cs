using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LmStudioTemplateRepairPlannerTests
{
    [Fact]
    public async Task PrefixOnlyContinuationFailureCreatesBuiltInV3PreviewWithoutLifecycleOrJournalWrites()
    {
        using var temporary = new TemporaryDirectory();
        PlannerFixture fixture = CreateFixture(temporary.Path);
        var controller = new PlanningOnlyInstanceController(fixture.Snapshot);
        var locator = new FakeModelFileLocator(new LmStudioModelFileResolutionAttempt(
            LmStudioModelFileResolutionStatus.Success,
            fixture.Resolution,
            "test success"));
        var store = new LmStudioTemplateTransactionStore(Path.Combine(temporary.Path, "transactions"));
        var planner = new LmStudioTemplateRepairPlanner(
            controller,
            new FixedTemplateReader(fixture.Analysis),
            new PromptTemplateRepairService(),
            store,
            locator);

        LmStudioTemplateRepairPlan plan = await planner.CreatePlanAsync(
            fixture.Model,
            PrefixOnlyContinuationFailure());

        Assert.Equal(1, controller.CaptureCount);
        Assert.Equal(0, controller.LifecycleMutationCount);
        Assert.Equal(1, locator.CallCount);
        Assert.Equal(fixture.Snapshot.Endpoint, locator.Endpoint);
        Assert.Equal(LmStudioRuntimeTemplateMode.BuiltIn, plan.OriginalRuntimeTemplate.Mode);
        Assert.Equal(PromptTemplateRepairStatus.Supported, plan.TemplatePreview.Status);
        Assert.Equal(PromptTemplateRepairService.CurrentRuleVersion, plan.TemplatePreview.RuleVersion);
        Assert.Contains("qwen-interleaved-instructions-v3", plan.TemplatePreview.PatchedTemplate, StringComparison.Ordinal);
        Assert.Empty(await store.ListAllAsync());
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "transactions")));
    }

    [Fact]
    public async Task NonExactFourStageBuiltInShapeIsRejectedBeforeLifecycleOrJournalWrites()
    {
        using var temporary = new TemporaryDirectory();
        PlannerFixture fixture = CreateFixture(temporary.Path);
        var controller = new PlanningOnlyInstanceController(fixture.Snapshot);
        var locator = new FakeModelFileLocator(new LmStudioModelFileResolutionAttempt(
            LmStudioModelFileResolutionStatus.Success,
            fixture.Resolution,
            "test success"));
        var store = new LmStudioTemplateTransactionStore(Path.Combine(temporary.Path, "transactions"));
        var planner = new LmStudioTemplateRepairPlanner(
            controller,
            new FixedTemplateReader(fixture.Analysis),
            new PromptTemplateRepairService(),
            store,
            locator);
        var malformed = new CodexInstructionHierarchyProbeResult(
            new CodexInstructionProbeStepResult(true, 200),
            new CodexInstructionProbeStepResult(true, 200),
            new CodexInstructionProbeStepResult(false, 500),
            new CodexInstructionProbeStepResult(false, null),
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
            "conversation failed instead of continuation",
            DateTimeOffset.Now);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            planner.CreatePlanAsync(fixture.Model, malformed));

        Assert.Contains("精确四阶段", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, controller.LifecycleMutationCount);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "transactions")));
    }

    [Fact]
    public async Task LocatorFailureKeepsManualFallbackAndDoesNotReadTemplateOrMutateLifecycle()
    {
        using var temporary = new TemporaryDirectory();
        PlannerFixture fixture = CreateFixture(temporary.Path);
        var controller = new PlanningOnlyInstanceController(fixture.Snapshot);
        var reader = new CountingTemplateReader(fixture.Analysis);
        var locator = new FakeModelFileLocator(new LmStudioModelFileResolutionAttempt(
            LmStudioModelFileResolutionStatus.IdentityMismatch,
            null,
            "稳定脱敏诊断"));
        string transactionDirectory = Path.Combine(temporary.Path, "transactions");
        var planner = new LmStudioTemplateRepairPlanner(
            controller,
            reader,
            new PromptTemplateRepairService(),
            new LmStudioTemplateTransactionStore(transactionDirectory),
            locator);

        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            planner.CreatePlanAsync(fixture.Model, PrefixOnlyContinuationFailure()));

        Assert.Contains("稳定脱敏诊断", exception.Message, StringComparison.Ordinal);
        Assert.Contains("手工选择", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, reader.CallCount);
        Assert.Equal(0, controller.LifecycleMutationCount);
        Assert.False(Directory.Exists(transactionDirectory));
    }

    [Fact]
    public async Task ProductionPlannerCreatesReadOnlyPersistentDefaultsPlanFromConcreteIdentity()
    {
        using var temporary = new TemporaryDirectory();
        PlannerFixture fixture = CreateFixture(temporary.Path);
        const string concreteIdentifier = "publisher/model.gguf";
        LmStudioModelFileResolution resolution = fixture.Resolution with { ConcreteModelIdentifier = concreteIdentifier };
        var controller = new PlanningOnlyInstanceController(fixture.Snapshot);
        var locator = new FakeModelFileLocator(new LmStudioModelFileResolutionAttempt(
            LmStudioModelFileResolutionStatus.Success,
            resolution,
            "test concrete identity"));
        var repair = new PromptTemplateRepairService();
        var defaultsStore = new LmStudioPerModelDefaultsStore(
            repair,
            new AtomicBatchWriter(),
            new PlannerProtector(),
            Path.Combine(temporary.Path, "defaults"));
        string defaultsPath = defaultsStore.GetDefaultsPath(concreteIdentifier);
        Directory.CreateDirectory(Path.GetDirectoryName(defaultsPath)!);
        await File.WriteAllTextAsync(defaultsPath, """
            {
              "preset": "",
              "operation": { "fields": [] },
              "load": { "fields": [ { "key": "llm.load.llama.evalBatchSize", "value": 4096 } ] },
              "unknown": { "preserved": true }
            }
            """);
        FileFingerprint before = await FileFingerprintService.CaptureAsync(defaultsPath);
        string transactionDirectory = Path.Combine(temporary.Path, "transactions");
        var planner = new LmStudioTemplateRepairPlanner(
            controller,
            new FixedTemplateReader(fixture.Analysis),
            repair,
            new LmStudioTemplateTransactionStore(transactionDirectory),
            locator,
            defaultsStore,
            () => "0.4.21.0");

        LmStudioTemplateRepairPlan plan = await planner.CreatePlanAsync(fixture.Model, PrefixOnlyContinuationFailure());

        LmStudioPerModelDefaultsPlan persistent = Assert.IsType<LmStudioPerModelDefaultsPlan>(plan.PersistentDefaults);
        Assert.Equal(LmStudioPerModelDefaultsMutation.Add, persistent.Mutation);
        Assert.Equal(concreteIdentifier, persistent.ConcreteModelIdentifier);
        Assert.Equal(defaultsPath, persistent.FilePath);
        Assert.Equal("0.4.21.0", plan.LmStudioVersion);
        Assert.Equal(before, await FileFingerprintService.CaptureAsync(defaultsPath));
        Assert.NotEqual(persistent.OriginalFingerprint.Sha256, persistent.CandidateFingerprint.Sha256);
        Assert.Equal(0, controller.LifecycleMutationCount);
        Assert.False(Directory.Exists(transactionDirectory));
    }

    private static PlannerFixture CreateFixture(string temporaryRoot)
    {
        string template = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "unsloth-qwen3.8-prefix-template.jinja"));
        string ggufPath = Path.Combine(temporaryRoot, "model.gguf");
        File.WriteAllText(ggufPath, "fixture");
        FileInfo file = new(ggufPath);
        var snapshot = new LmStudioLoadedInstanceSnapshot(
            new Uri("http://127.0.0.1:1234"),
            "qwen3.8-27b@q6_k_xl",
            "qwen3.8-27b@q6_k_xl",
            null,
            "qwen35",
            "Q6_K_XL",
            "27B",
            "llm",
            262_144,
            new LmStudioLoadConfiguration(ContextLength: 161_024),
            null,
            false,
            DateTimeOffset.Now,
            "snapshot-fingerprint",
            new LmStudioLoadTarget(
                "qwen3.8-27b@q6_k_xl",
                null,
                [],
                "qwen35",
                "Q6_K_XL",
                "27B",
                "gguf",
                262_144,
                "target-fingerprint"));
        var model = new ModelProfile(
            snapshot.InstanceId,
            "Qwen3.8 27B UD",
            ProviderKind.LmStudio,
            Quantization: snapshot.Quantization,
            Parameters: snapshot.Parameters,
            IsLoaded: true,
            MaxContextLength: snapshot.MaxContextLength,
            LoadedContextLength: snapshot.LoadConfiguration.ContextLength,
            LoadedInstanceId: snapshot.InstanceId,
            Architecture: snapshot.Architecture,
            ModelType: snapshot.ModelType,
            SourceModelKey: snapshot.SourceModelKey,
            LoadedConfiguration: snapshot.LoadConfiguration,
            Format: "gguf");
        var resolution = new LmStudioModelFileResolution(
            ggufPath,
            snapshot.SourceModelKey,
            snapshot.SelectedVariant,
            snapshot.Architecture,
            snapshot.Quantization,
            "lms ps --json");
        var analysis = new GgufChatTemplateAnalysis(
            ggufPath,
            file.Name,
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc),
            3,
            "Qwen Fixture",
            "qwen35",
            template,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template))));
        return new PlannerFixture(model, snapshot, resolution, analysis);
    }

    private static CodexInstructionHierarchyProbeResult PrefixOnlyContinuationFailure() => new(
        new CodexInstructionProbeStepResult(true, 200),
        new CodexInstructionProbeStepResult(true, 200),
        new CodexInstructionProbeStepResult(true, 200),
        new CodexInstructionProbeStepResult(false, 500),
        CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
        "prefix-only continuation failure",
        DateTimeOffset.Now);

    private sealed record PlannerFixture(
        ModelProfile Model,
        LmStudioLoadedInstanceSnapshot Snapshot,
        LmStudioModelFileResolution Resolution,
        GgufChatTemplateAnalysis Analysis);

    private sealed class FakeModelFileLocator(LmStudioModelFileResolutionAttempt attempt) : ILmStudioModelFileLocator
    {
        public int CallCount { get; private set; }

        public Uri? Endpoint { get; private set; }

        public Task<LmStudioModelFileResolutionAttempt> ResolveAsync(
            ModelProfile model,
            Uri endpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Endpoint = endpoint;
            return Task.FromResult(attempt);
        }
    }

    private sealed class PlanningOnlyInstanceController(LmStudioLoadedInstanceSnapshot snapshot) : ILmStudioInstanceController
    {
        public int CaptureCount { get; private set; }

        public int LifecycleMutationCount { get; private set; }

        public Task<LmStudioLoadedInstanceSnapshot> CaptureAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            Assert.Equal(snapshot.InstanceId, instanceId);
            return Task.FromResult(snapshot);
        }

        public Task<LmStudioTemplateRepairResult> ApplyTemplateAsync(
            LmStudioTemplateRepairPlan plan,
            CancellationToken cancellationToken = default)
        {
            LifecycleMutationCount++;
            throw new NotSupportedException();
        }

        public Task<LmStudioRollbackResult> RollbackAsync(
            LmStudioTemplateRepairPlan plan,
            string? patchedInstanceId,
            CancellationToken cancellationToken = default)
        {
            LifecycleMutationCount++;
            throw new NotSupportedException();
        }

        public Task<LmStudioRollbackResult> RecoverAsync(
            LmStudioTemplateTransactionRecord transaction,
            CancellationToken cancellationToken = default)
        {
            LifecycleMutationCount++;
            throw new NotSupportedException();
        }

        public Task<LmStudioRecoveryAssessment> AssessRecoveryAsync(
            LmStudioTemplateTransactionRecord transaction,
            CancellationToken cancellationToken = default)
        {
            LifecycleMutationCount++;
            throw new NotSupportedException();
        }

        public Task<LmStudioRollbackResult> RecoverAsync(
            LmStudioTemplateTransactionRecord transaction,
            LmStudioRecoveryAssessment assessment,
            CancellationToken cancellationToken = default)
        {
            LifecycleMutationCount++;
            throw new NotSupportedException();
        }

        public Task CompleteAsync(Guid transactionId, CancellationToken cancellationToken = default)
        {
            LifecycleMutationCount++;
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedTemplateReader(GgufChatTemplateAnalysis analysis) : IGgufChatTemplateReader
    {
        public Task<GgufChatTemplateAnalysis> ReadAsync(
            string filePath,
            CancellationToken cancellationToken = default) => Task.FromResult(analysis);
    }

    private sealed class CountingTemplateReader(GgufChatTemplateAnalysis analysis) : IGgufChatTemplateReader
    {
        public int CallCount { get; private set; }

        public Task<GgufChatTemplateAnalysis> ReadAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(analysis);
        }
    }

    private sealed class PlannerProtector : ILmStudioDefaultsProtector
    {
        public byte[] Protect(byte[] plaintext) => [0x43, 0x4D, 0x4D, .. plaintext];

        public byte[] Unprotect(byte[] ciphertext) => ciphertext[3..];
    }
}
