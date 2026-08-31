using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LiveLmStudioIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveLmStudio")]
    public async Task CurrentIncompleteTransactionRecoveryAssessmentIsReadOnly()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM=1 to run the live LM Studio test.");
        var transactions = new LmStudioTemplateTransactionStore(new AppPaths());
        IReadOnlyList<LmStudioTemplateTransactionRecord> incomplete = await transactions.ListIncompleteAsync(TestContext.Current.CancellationToken);
        if (incomplete.Count != 1)
        {
            Assert.Skip($"Expected exactly one incomplete journal for a focused read-only assessment; found {incomplete.Count}.");
            return;
        }

        LmStudioTemplateTransactionRecord transaction = incomplete[0];
        if (transaction.OriginalInstance.RequiresAuthentication)
        {
            Assert.Skip("The live recovery journal requires LM Studio authentication; this read-only fixture never reads credentials.");
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new LmStudioClient(transaction.OriginalInstance.Endpoint, null, http);
        string[] beforeIds = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .Where(model => model.IsLoaded == true)
            .Select(model => model.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var home = new DefaultCodexHomeProvider();
        var runtime = new CodexRuntimeProbe(home, new TomlConfigPatchEngine());
        var reader = new GgufChatTemplateReader();
        var repair = new PromptTemplateRepairService(reader);
        using var controller = new LmStudioInstanceController(
            transaction.OriginalInstance.Endpoint,
            false,
            http,
            null,
            runtime,
            reader,
            repair,
            transactions,
            new FakeLogger());

        LmStudioRecoveryAssessment assessment = await controller.AssessRecoveryAsync(transaction, TestContext.Current.CancellationToken);

        string[] afterIds = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .Where(model => model.IsLoaded == true)
            .Select(model => model.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(beforeIds, afterIds);
        if (transaction.SchemaVersion == 1 &&
            transaction.State == LmStudioTemplateTransactionState.RollbackFailed &&
            assessment.Candidates.Count == 1 &&
            assessment.Candidates[0].Snapshot.InstanceId.Equals(transaction.OriginalInstance.InstanceId, StringComparison.Ordinal) &&
            assessment.Candidates[0].MatchesOriginalSnapshot &&
            assessment.Candidates[0].ReproducesOriginalFailure)
        {
            Assert.Equal(LmStudioRecoveryDisposition.AlreadyRestored, assessment.Disposition);
            Assert.False(assessment.RequiresLifecycleMutation);
        }
    }

    [Fact]
    [Trait("Category", "LiveLmStudio")]
    public async Task CurrentLoadedLmStudioModelResolvesExactVariantGguf()
    {
        const string expectedSourceModelKey = "qwen3.8-flash-next@iq4_xs";
        const string expectedConcreteIdentifier = "unsloth/Qwen3.8-Flash-Next-GGUF/Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf";
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM=1 to run the live LM Studio test.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        ModelProfile loaded = Assert.Single(
            await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken),
            model => model.IsLoaded == true &&
                model.ModelType == "llm" &&
                string.Equals(model.SourceModelKey, expectedSourceModelKey, StringComparison.OrdinalIgnoreCase));

        var locator = new LmStudioModelFileLocator();
        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(
            loaded,
            new Uri("http://127.0.0.1:1234"),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.Succeeded, attempt.Diagnostic);
        LmStudioModelFileResolution resolution = Assert.IsType<LmStudioModelFileResolution>(attempt.Resolution);
        Assert.Equal("lms ps --json", resolution.Source);
        Assert.True(File.Exists(resolution.FilePath));
        Assert.Equal(loaded.SourceModelKey, resolution.SourceModelKey, ignoreCase: true);
        Assert.Equal(loaded.SelectedVariant, resolution.SelectedVariant, ignoreCase: true);
        Assert.Equal(loaded.Quantization, resolution.Quantization, ignoreCase: true);
        Assert.NotNull(resolution.ConcreteModelIdentifier);
        string concreteIdentifier = resolution.ConcreteModelIdentifier.Replace('\\', '/').TrimStart('.', '/');
        string normalizedFilePath = Path.GetFullPath(resolution.FilePath).Replace('\\', '/');
        Assert.Equal(expectedSourceModelKey, loaded.SourceModelKey, ignoreCase: true);
        Assert.Equal(expectedConcreteIdentifier, concreteIdentifier, ignoreCase: true);
        Assert.EndsWith(concreteIdentifier, normalizedFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFileName(resolution.FilePath), Path.GetFileName(concreteIdentifier), ignoreCase: true);
        GgufChatTemplateAnalysis analysis = await new GgufChatTemplateReader().ReadAsync(resolution.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(loaded.Architecture, analysis.Architecture, ignoreCase: true);
        Assert.Equal(PromptTemplateRepairStatus.Supported, new PromptTemplateRepairService().CreatePreview(analysis).Status);
    }

    [Fact]
    [Trait("Category", "LiveLmStudio")]
    public async Task CurrentPersistentDefaultsPreviewIsReadOnlyAndUsesConcreteIdentity()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM=1 to run the live LM Studio test.");
        Uri endpoint = new("http://127.0.0.1:1234");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new LmStudioClient(endpoint, null, http);
        ModelProfile loaded = Assert.Single(
            await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken),
            model => model.IsLoaded == true && model.ModelType == "llm");
        var locator = new LmStudioModelFileLocator();
        LmStudioModelFileResolutionAttempt resolutionAttempt = await locator.ResolveAsync(loaded, endpoint, TestContext.Current.CancellationToken);
        LmStudioModelFileResolution resolution = Assert.IsType<LmStudioModelFileResolution>(resolutionAttempt.Resolution);
        var hierarchyProbe = new CodexModelManager.Core.Providers.CodexInstructionHierarchyProbe(http, endpoint);
        CodexInstructionHierarchyProbeResult originalProbe = await hierarchyProbe.ProbeAsync(loaded.Id, TestContext.Current.CancellationToken);
        Assert.Contains(originalProbe.FailureCode, new[]
        {
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
            CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole,
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder,
        });

        var reader = new GgufChatTemplateReader();
        var repair = new PromptTemplateRepairService(reader);
        var defaultsStore = new LmStudioPerModelDefaultsStore(repair, new AtomicBatchWriter());
        string defaultsPath = defaultsStore.GetDefaultsPath(Assert.IsType<string>(resolution.ConcreteModelIdentifier));
        FileFingerprint defaultsBefore = await FileFingerprintService.CaptureAsync(defaultsPath, TestContext.Current.CancellationToken);
        string[] instanceIdsBefore = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .Where(model => model.IsLoaded == true)
            .Select(model => model.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var transactions = new LmStudioTemplateTransactionStore(new AppPaths());
        var runtime = new CodexRuntimeProbe(new DefaultCodexHomeProvider(), new TomlConfigPatchEngine());
        using var controller = new LmStudioInstanceController(
            endpoint,
            false,
            http,
            null,
            runtime,
            reader,
            repair,
            transactions,
            new FakeLogger(),
            defaultsStore,
            LmStudioLocalVersionDetector.Detect,
            locator);
        var planner = new LmStudioTemplateRepairPlanner(
            controller,
            reader,
            repair,
            transactions,
            locator,
            defaultsStore,
            LmStudioLocalVersionDetector.Detect);

        LmStudioTemplateRepairPlan plan = await planner.CreatePlanAsync(loaded, originalProbe, TestContext.Current.CancellationToken);

        LmStudioPerModelDefaultsPlan persistent = Assert.IsType<LmStudioPerModelDefaultsPlan>(plan.PersistentDefaults);
        Assert.Equal(LmStudioPerModelDefaultsMutation.Add, persistent.Mutation);
        Assert.Equal(defaultsPath, persistent.FilePath, ignoreCase: true);
        Assert.Equal("12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE", plan.GgufAnalysis.TemplateSha256, ignoreCase: true);
        Assert.Equal("9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85", persistent.TargetTemplateSha256, ignoreCase: true);
        Assert.Equal(defaultsBefore, await FileFingerprintService.CaptureAsync(defaultsPath, TestContext.Current.CancellationToken));
        Assert.Equal(instanceIdsBefore, (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .Where(model => model.IsLoaded == true)
            .Select(model => model.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        Assert.False(File.Exists(transactions.GetPath(plan.TransactionId)));
    }

    [Fact]
    [Trait("Category", "LiveLmStudio")]
    public async Task CurrentV2RuntimeCanCreateReadOnlyV3UpgradePlanFromCompletedProvenance()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM=1 to run the live LM Studio test.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        Uri endpoint = new("http://127.0.0.1:1234");
        var client = new LmStudioClient(endpoint, null, http);
        ModelProfile? loaded = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .FirstOrDefault(model => model.IsLoaded == true && model.ModelType == "llm");
        Assert.NotNull(loaded);
        var hierarchyProbe = new CodexModelManager.Core.Providers.CodexInstructionHierarchyProbe(http, endpoint);
        CodexInstructionHierarchyProbeResult before = await hierarchyProbe.ProbeAsync(loaded.Id, TestContext.Current.CancellationToken);
        if (before.FailureCode != CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder)
        {
            Assert.Skip($"Current runtime is not a v2 upgrade candidate: compatible={before.IsCompatible}, failure={before.FailureCode ?? "none"}.");
            return;
        }

        string[] beforeIds = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .Where(model => model.IsLoaded == true)
            .Select(model => model.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var reader = new GgufChatTemplateReader();
        var repair = new PromptTemplateRepairService(reader);
        var transactions = new LmStudioTemplateTransactionStore(new AppPaths());
        var home = new DefaultCodexHomeProvider();
        var runtime = new CodexRuntimeProbe(home, new TomlConfigPatchEngine());
        using var controller = new LmStudioInstanceController(endpoint, false, http, null, runtime, reader, repair, transactions, new FakeLogger());
        var planner = new LmStudioTemplateRepairPlanner(controller, reader, repair, transactions);

        LmStudioTemplateRepairPlan plan = await planner.CreatePlanAsync(loaded, before, TestContext.Current.CancellationToken);

        string[] afterIds = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .Where(model => model.IsLoaded == true)
            .Select(model => model.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(beforeIds, afterIds);
        Assert.Equal(LmStudioRuntimeTemplateMode.ManagerRule, plan.OriginalRuntimeTemplate.Mode);
        Assert.Equal(PromptTemplateRepairService.LegacyLeadingRuleVersion, plan.OriginalRuntimeTemplate.RuleVersion);
        Assert.Equal(PromptTemplateRepairService.CurrentRuleVersion, plan.TemplatePreview.RuleVersion);
        Assert.NotNull(plan.OriginalRuntimeTemplate.EvidenceTransactionId);
        Assert.NotNull(plan.OriginalRuntimeTemplateText);
        Assert.False(File.Exists(transactions.GetPath(plan.TransactionId)));
    }

    [Fact]
    [Trait("Category", "LiveLmStudio")]
    public async Task CurrentLmStudioDiscoveryAndResponsesCompatibility()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM=1 to run the live LM Studio test.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new LmStudioClient(new Uri("http://127.0.0.1:1234"), null, http);
        ProviderProbeResult probe = await client.ProbeAsync(TestContext.Current.CancellationToken);
        Assert.True(probe.IsAvailable, probe.Summary);
        IReadOnlyList<ModelProfile> models = await client.DiscoverModelsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(models);
        ModelProfile? loaded = models.FirstOrDefault(model => model.IsLoaded == true && model.ModelType == "llm" && model.LoadedContextLength is not null);
        if (loaded is null)
        {
            Assert.Skip("LM Studio native API 当前未报告 loaded_instances；不把 lms ps 或理论模型列表猜作 Server 实际 loaded context。");
            return;
        }

        Assert.NotNull(loaded.LoadedContextLength);
        Assert.True(loaded.LoadedContextLength <= loaded.MaxContextLength);
        CompatibilityReport report = await client.TestCompatibilityAsync(loaded.Id, TestContext.Current.CancellationToken);
        CompatibilityResult responses = report.Results.Single(result => result.Capability == "Responses");
        CompatibilityResult hierarchy = report.Results.Single(result => result.Capability == "Codex Instruction Hierarchy");
        Assert.True(responses.Status == CompatibilityStatus.Supported, responses.Detail);
        if (hierarchy.Status == CompatibilityStatus.Failed)
        {
            Assert.Contains(hierarchy.FailureCode, new[]
            {
                CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
                CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole,
                CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder,
            });
            Assert.Equal(CompatibilityStatus.Failed, report.Results.Single(result => result.Capability == "Codex Agent").Status);
            return;
        }

        CompatibilityResult streaming = report.Results.Single(result => result.Capability == "Streaming");
        CompatibilityResult tools = report.Results.Single(result => result.Capability == "Tool Calling");
        Assert.True(streaming.Status == CompatibilityStatus.Supported, streaming.Detail);
        Assert.True(tools.Status == CompatibilityStatus.Supported, tools.Detail);
    }

    [Fact]
    [Trait("Category", "LiveLmStudioMutation")]
    public async Task TransactionalRuntimeTemplateRepairPassesHierarchyAndRollsBack()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("CMM_RUN_LIVE_LM_MUTATION"), "1", StringComparison.Ordinal), "Set CMM_RUN_LIVE_LM_MUTATION=1 only after closing Codex to run the reversible load/unload test.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Uri endpoint = new("http://127.0.0.1:1234");
        var client = new LmStudioClient(endpoint, null, http);
        ModelProfile? loaded = (await client.DiscoverNativeModelsAsync(TestContext.Current.CancellationToken))
            .FirstOrDefault(model => model.IsLoaded == true && model.ModelType == "llm" && model.LoadedContextLength is not null);
        Assert.NotNull(loaded);
        var hierarchyProbe = new CodexModelManager.Core.Providers.CodexInstructionHierarchyProbe(http, endpoint);
        CodexInstructionHierarchyProbeResult before = await hierarchyProbe.ProbeAsync(loaded.Id, TestContext.Current.CancellationToken);
        Assert.Contains(before.FailureCode, new[]
        {
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
            CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole,
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder,
        });

        var home = new DefaultCodexHomeProvider();
        var runtime = new CodexRuntimeProbe(home, new TomlConfigPatchEngine());
        var reader = new GgufChatTemplateReader();
        var repair = new PromptTemplateRepairService(reader);
        var transactions = new LmStudioTemplateTransactionStore(new AppPaths());
        using var controller = new LmStudioInstanceController(endpoint, false, http, null, runtime, reader, repair, transactions, new FakeLogger());
        var planner = new LmStudioTemplateRepairPlanner(controller, reader, repair, transactions);
        LmStudioTemplateRepairPlan plan = await planner.CreatePlanAsync(loaded, before, TestContext.Current.CancellationToken);
        LmStudioTemplateRepairResult? applied = null;
        try
        {
            applied = await controller.ApplyTemplateAsync(plan, TestContext.Current.CancellationToken);
            Assert.True(applied.HierarchyProbe.IsCompatible, applied.HierarchyProbe.Detail);
            Assert.Equal(loaded.LoadedContextLength, applied.PatchedInstance.LoadConfiguration.ContextLength);
        }
        finally
        {
            if (applied is not null)
            {
                LmStudioRollbackResult rollback = await controller.RollbackAsync(plan, applied.PatchedInstance.InstanceId, CancellationToken.None);
                Assert.True(rollback.Succeeded, rollback.Detail);
            }
        }
    }
}
