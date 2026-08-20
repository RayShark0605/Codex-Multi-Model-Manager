using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.LmStudio;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Tests;

public sealed class LmStudioInstanceControllerTests
{
    [Fact]
    public async Task ApplyUsesPatchedObjectPreservesConfigAndCanCompleteTransaction()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateRepairResult result = await fixture.Controller.ApplyTemplateAsync(plan);

        Assert.Equal("qwen/root:2", result.PatchedInstance.InstanceId);
        Assert.True(result.HierarchyProbe.IsCompatible);
        Assert.Single(fixture.Handler.LoadBodies);
        using (JsonDocument load = JsonDocument.Parse(fixture.Handler.LoadBodies[0]))
        {
            JsonElement root = load.RootElement;
            Assert.Equal("qwen/root", root.GetProperty("model").GetString());
            Assert.Equal(32_768, root.GetProperty("context_length").GetInt32());
            Assert.Equal(4_096, root.GetProperty("eval_batch_size").GetInt32());
            Assert.False(root.TryGetProperty("config", out _));
            Assert.Equal(JsonValueKind.Object, root.GetProperty("prompt_template").ValueKind);
            Assert.Equal("patched-template", root.GetProperty("prompt_template").GetProperty("template").GetString());
        }

        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.PatchedAndVerified, record.State);
        Assert.Equal(result.PatchedInstance.InstanceId, record.PatchedInstanceId);
        Assert.Equal("qwen/root", record.LoadModelKey);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(record.SameSourceInstanceIdsBeforeLoad));
        Assert.Equal([result.PatchedInstance.InstanceId], record.SameSourceInstanceIdsAfterLoad);
        Assert.Equal(result.PatchedInstance.InstanceId, record.CandidateInstanceId);
        string journal = await File.ReadAllTextAsync(fixture.Store.GetPath(plan.TransactionId));
        Assert.DoesNotContain("patched-template", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("original-template", journal, StringComparison.Ordinal);

        await fixture.Controller.CompleteAsync(plan.TransactionId);
        record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.Completed, record.State);
    }

    [Fact]
    public async Task FinalCancellationRollbackUnloadsPatchAndReloadsWithoutPromptTemplate()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateRepairResult applied = await fixture.Controller.ApplyTemplateAsync(plan);

        LmStudioRollbackResult rollback = await fixture.Controller.RollbackAsync(plan, applied.PatchedInstance.InstanceId);

        Assert.True(rollback.Succeeded, rollback.Detail);
        Assert.Equal("qwen/root:3", rollback.RestoredInstance?.InstanceId);
        Assert.Collection(fixture.Handler.LoadBodies, _ => { }, _ => { });
        using JsonDocument restoredLoad = JsonDocument.Parse(fixture.Handler.LoadBodies[1]);
        Assert.False(restoredLoad.RootElement.TryGetProperty("prompt_template", out _));
        Assert.Equal("qwen/root", restoredLoad.RootElement.GetProperty("model").GetString());
        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.RolledBack, record.State);
        Assert.Empty(await fixture.Store.ListIncompleteAsync());
    }

    [Fact]
    public async Task V2UpgradeRollbackRestoresV2ObjectTemplateAndExactFourStageSignature()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = await fixture.CreateV2UpgradePlanAsync(original);

        LmStudioTemplateRepairResult applied = await fixture.Controller.ApplyTemplateAsync(plan);
        LmStudioRollbackResult rollback = await fixture.Controller.RollbackAsync(plan, applied.PatchedInstance.InstanceId);

        Assert.True(applied.HierarchyProbe.IsCompatible);
        Assert.True(rollback.Succeeded, rollback.Detail);
        Assert.Collection(fixture.Handler.LoadBodies, _ => { }, _ => { });
        using JsonDocument restoredLoad = JsonDocument.Parse(fixture.Handler.LoadBodies[1]);
        Assert.Equal(
            ControllerFixture.LegacyV2Template,
            restoredLoad.RootElement.GetProperty("prompt_template").GetProperty("template").GetString());
        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(3, record.SchemaVersion);
        Assert.Equal(LmStudioRuntimeTemplateMode.ManagerRule, record.OriginalRuntimeTemplateMode);
        Assert.Equal(PromptTemplateRepairService.LegacyLeadingRuleVersion, record.OriginalRuntimeRuleVersion);
        Assert.Equal(PromptTemplateRepairService.CurrentRuleVersion, record.TargetRuntimeRuleVersion);
        Assert.Equal(LmStudioTemplateTransactionState.RolledBack, record.State);
    }

    [Fact]
    public async Task MissingV2CompletedProvenanceBlocksBeforeUnloadAndJournalWrite()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        fixture.Handler.SetRuntimeTemplate(ControllerFixture.LegacyV2Template);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original) with
        {
            FailureCode = CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder,
            OriginalRuntimeTemplate = new LmStudioRuntimeTemplateProvenance(
                LmStudioRuntimeTemplateMode.ManagerRule,
                PromptTemplateRepairService.LegacyLeadingRuleVersion,
                ControllerFixture.HashTemplate(ControllerFixture.LegacyV2Template),
                Guid.NewGuid()),
            OriginalHierarchyProbe = ControllerFixture.V2FailureProbe(),
            OriginalRuntimeTemplateText = ControllerFixture.LegacyV2Template,
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.False(File.Exists(fixture.Store.GetPath(plan.TransactionId)));
    }

    [Fact]
    public async Task SchemaV3CrashRecoveryRebuildsAndRestoresV2InsteadOfBuiltInTemplate()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = await fixture.CreateV2UpgradePlanAsync(original);
        var record = new LmStudioTemplateTransactionRecord(
            3,
            plan.TransactionId,
            LmStudioTemplateTransactionState.OriginalUnloaded,
            plan.CreatedAt,
            DateTimeOffset.Now,
            original,
            plan.FailureCode,
            plan.GgufAnalysis.FilePath,
            plan.GgufAnalysis.FileName,
            plan.GgufAnalysis.FileLength,
            plan.GgufAnalysis.LastWriteTimeUtc,
            plan.GgufAnalysis.GgufVersion,
            plan.GgufAnalysis.TemplateSha256,
            plan.TemplatePreview.PatchedTemplateSha256!,
            plan.TemplatePreview.RuleVersion,
            null,
            "simulated v3 crash after original unload",
            LoadModelKey: original.SourceModelKey,
            LastStableState: LmStudioTemplateTransactionState.OriginalUnloaded,
            OriginalRuntimeTemplateMode: plan.OriginalRuntimeTemplate.Mode,
            OriginalRuntimeRuleVersion: plan.OriginalRuntimeTemplate.RuleVersion,
            OriginalRuntimeTemplateSha256: plan.OriginalRuntimeTemplate.TemplateSha256,
            OriginalRuntimeEvidenceTransactionId: plan.OriginalRuntimeTemplate.EvidenceTransactionId,
            TargetRuntimeRuleVersion: plan.TemplatePreview.RuleVersion,
            OriginalHierarchyProbe: plan.OriginalHierarchyProbe);
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateNoLoadedInstance();

        LmStudioRollbackResult recovered = await fixture.Controller.RecoverAsync(record);

        Assert.True(recovered.Succeeded, recovered.Detail);
        Assert.Single(fixture.Handler.LoadBodies);
        using JsonDocument load = JsonDocument.Parse(fixture.Handler.LoadBodies[0]);
        Assert.Equal(
            ControllerFixture.LegacyV2Template,
            load.RootElement.GetProperty("prompt_template").GetProperty("template").GetString());
        Assert.Equal(LmStudioTemplateTransactionState.RolledBack, (await fixture.Store.ReadAsync(plan.TransactionId))?.State);
    }

    [Fact]
    public async Task SchemaV3SameIdAlreadyRestoredV2ClosesJournalWithoutReload()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = await fixture.CreateV2UpgradePlanAsync(original);
        var record = new LmStudioTemplateTransactionRecord(
            3,
            plan.TransactionId,
            LmStudioTemplateTransactionState.PatchedAndVerified,
            plan.CreatedAt,
            DateTimeOffset.Now,
            original,
            plan.FailureCode,
            plan.GgufAnalysis.FilePath,
            plan.GgufAnalysis.FileName,
            plan.GgufAnalysis.FileLength,
            plan.GgufAnalysis.LastWriteTimeUtc,
            plan.GgufAnalysis.GgufVersion,
            plan.GgufAnalysis.TemplateSha256,
            plan.TemplatePreview.PatchedTemplateSha256!,
            plan.TemplatePreview.RuleVersion,
            original.InstanceId,
            "simulated same-ID instance already restored to v2",
            LoadModelKey: original.SourceModelKey,
            LastStableState: LmStudioTemplateTransactionState.PatchedAndVerified,
            OriginalRuntimeTemplateMode: plan.OriginalRuntimeTemplate.Mode,
            OriginalRuntimeRuleVersion: plan.OriginalRuntimeTemplate.RuleVersion,
            OriginalRuntimeTemplateSha256: plan.OriginalRuntimeTemplate.TemplateSha256,
            OriginalRuntimeEvidenceTransactionId: plan.OriginalRuntimeTemplate.EvidenceTransactionId,
            TargetRuntimeRuleVersion: plan.TemplatePreview.RuleVersion,
            OriginalHierarchyProbe: plan.OriginalHierarchyProbe);
        await fixture.Store.WriteAsync(record);

        LmStudioRecoveryAssessment assessment = await fixture.Controller.AssessRecoveryAsync(record);
        LmStudioRollbackResult recovered = await fixture.Controller.RecoverAsync(record, assessment);

        Assert.Equal(LmStudioRecoveryDisposition.AlreadyRestored, assessment.Disposition);
        Assert.False(assessment.RequiresLifecycleMutation);
        Assert.True(recovered.Succeeded, recovered.Detail);
        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.Equal(LmStudioTemplateTransactionState.RolledBack, (await fixture.Store.ReadAsync(plan.TransactionId))?.State);
    }

    [Fact]
    public async Task HierarchyFailureAutomaticallyRollsBackOriginalTemplateInstance()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: false);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateApplyException exception = await Assert.ThrowsAsync<LmStudioTemplateApplyException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.True(exception.Rollback.Succeeded, exception.Rollback.Detail);
        Assert.Collection(fixture.Handler.LoadBodies, _ => { }, _ => { });
        using JsonDocument rollbackLoad = JsonDocument.Parse(fixture.Handler.LoadBodies[1]);
        Assert.False(rollbackLoad.RootElement.TryGetProperty("prompt_template", out _));
        Assert.Equal("qwen/root:3", fixture.Handler.CurrentInstanceId);
        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.RolledBack, record.State);
    }

    [Fact]
    public async Task LostLoadEchoWithReusedInstanceIdStillUnloadsSuspectedPatchBeforeRollback()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, firstLoadOmitsConfig: true, firstLoadReusesOriginalId: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateApplyException exception = await Assert.ThrowsAsync<LmStudioTemplateApplyException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.True(exception.Rollback.Succeeded, exception.Rollback.Detail);
        Assert.Collection(fixture.Handler.LoadBodies, _ => { }, _ => { });
        Assert.Equal("qwen/root:3", fixture.Handler.CurrentInstanceId);
        using JsonDocument rollbackLoad = JsonDocument.Parse(fixture.Handler.LoadBodies[1]);
        Assert.False(rollbackLoad.RootElement.TryGetProperty("prompt_template", out _));
    }

    [Fact]
    public async Task InstanceConfigDriftBlocksBeforeUnloadOrTransactionWrite()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        fixture.Handler.CurrentContextLength = 65_536;

        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.False(File.Exists(fixture.Store.GetPath(plan.TransactionId)));
        Assert.Equal(ControllerFixture.OriginalInstanceId, fixture.Handler.CurrentInstanceId);
    }

    [Fact]
    public async Task SelectedVariantDriftBlocksBeforeUnloadOrTransactionWrite()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        fixture.Handler.CurrentSelectedVariant = "qwen/root@q4_k_m";

        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.False(File.Exists(fixture.Store.GetPath(plan.TransactionId)));
    }

    [Fact]
    public async Task VariantDriftAfterOriginalUnloadSendsNoPatchedLoadAndPreservesStableStage()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        fixture.Handler.SelectedVariantAfterNextUnload = "qwen/root@q4_k_m";

        LmStudioTemplateApplyException failure = await Assert.ThrowsAsync<LmStudioTemplateApplyException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.False(failure.Rollback.Succeeded);
        Assert.Equal(1, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.RollbackFailed, record.State);
        Assert.Equal(LmStudioTemplateTransactionState.OriginalUnloaded, record.LastStableState);
        Assert.Equal(LmStudioLifecycleStage.LoadPatched, record.FailureStage);
        Assert.Equal(LmStudioLifecycleStage.LoadOriginal, record.LastRecoveryFailureStage);
    }

    [Fact]
    public async Task CrashRecoveryFromOriginalUnloadedReloadsWithoutTemplateAndClosesJournal()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        var record = new LmStudioTemplateTransactionRecord(
            1,
            plan.TransactionId,
            LmStudioTemplateTransactionState.OriginalUnloaded,
            plan.CreatedAt,
            DateTimeOffset.Now,
            original,
            plan.FailureCode,
            plan.GgufAnalysis.FilePath,
            plan.GgufAnalysis.FileName,
            plan.GgufAnalysis.FileLength,
            plan.GgufAnalysis.LastWriteTimeUtc,
            plan.GgufAnalysis.GgufVersion,
            plan.GgufAnalysis.TemplateSha256,
            plan.TemplatePreview.PatchedTemplateSha256!,
            plan.TemplatePreview.RuleVersion,
            null,
            "simulated crash");
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateNoLoadedInstance();

        LmStudioRollbackResult recovered = await fixture.Controller.RecoverAsync(record);

        Assert.True(recovered.Succeeded, recovered.Detail);
        Assert.Single(fixture.Handler.LoadBodies);
        using JsonDocument load = JsonDocument.Parse(fixture.Handler.LoadBodies[0]);
        Assert.False(load.RootElement.TryGetProperty("prompt_template", out _));
        Assert.Empty(await fixture.Store.ListIncompleteAsync());
    }

    [Fact]
    public async Task PatchedLoadFailureRestoresOriginalVariantAndConfiguration()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, firstLoadFails: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateApplyException exception = await Assert.ThrowsAsync<LmStudioTemplateApplyException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.True(exception.Rollback.Succeeded, exception.Rollback.Detail);
        Assert.Equal("qwen/root:3", fixture.Handler.CurrentInstanceId);
        Assert.Collection(fixture.Handler.LoadBodies, _ => { }, _ => { });
        using JsonDocument restored = JsonDocument.Parse(fixture.Handler.LoadBodies[1]);
        Assert.Equal("qwen/root", restored.RootElement.GetProperty("model").GetString());
        Assert.False(restored.RootElement.TryGetProperty("prompt_template", out _));
    }

    [Fact]
    public async Task PatchAndRollbackLoadFailuresRetainOriginalUnloadedStableState()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, firstLoadFails: true, secondLoadFails: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateApplyException exception = await Assert.ThrowsAsync<LmStudioTemplateApplyException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.False(exception.Rollback.Succeeded);
        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(3, record.SchemaVersion);
        Assert.Equal("qwen/root", record.LoadModelKey);
        Assert.Equal(LmStudioTemplateTransactionState.RollbackFailed, record.State);
        Assert.Equal(LmStudioTemplateTransactionState.OriginalUnloaded, record.LastStableState);
        Assert.Equal(LmStudioLifecycleStage.LoadOriginal, record.LastRecoveryFailureStage);
        Assert.Equal(2, fixture.Handler.LoadBodies.Count);
    }

    [Fact]
    public async Task RollbackLoadFailureLeavesAccurateRollbackFailedJournal()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, secondLoadFails: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateRepairResult applied = await fixture.Controller.ApplyTemplateAsync(plan);

        LmStudioRollbackResult rollback = await fixture.Controller.RollbackAsync(plan, applied.PatchedInstance.InstanceId);

        Assert.False(rollback.Succeeded);
        Assert.Null(fixture.Handler.CurrentInstanceId);
        LmStudioTemplateTransactionRecord record = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.RollbackFailed, record.State);
        Assert.Equal(applied.PatchedInstance.InstanceId, record.PatchedInstanceId);
        Assert.Contains("HTTP 500", record.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewInstanceIdWithoutNumericSuffixIsTakenFromLoadResponse()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, firstLoadWithoutSuffix: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateRepairResult applied = await fixture.Controller.ApplyTemplateAsync(plan);

        Assert.Equal("runtime-patched", applied.PatchedInstance.InstanceId);
        LmStudioRollbackResult rollback = await fixture.Controller.RollbackAsync(plan, applied.PatchedInstance.InstanceId);
        Assert.True(rollback.Succeeded, rollback.Detail);
    }

    [Fact]
    public async Task RemainingTtlIsSentAsActionTimeTtlSeconds()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, remainingTtlSeconds: 117);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateRepairResult applied = await fixture.Controller.ApplyTemplateAsync(plan);

        using JsonDocument load = JsonDocument.Parse(fixture.Handler.LoadBodies[0]);
        Assert.Equal(117, load.RootElement.GetProperty("ttl_seconds").GetInt32());
        LmStudioRollbackResult rollback = await fixture.Controller.RollbackAsync(plan, applied.PatchedInstance.InstanceId);
        Assert.True(rollback.Succeeded, rollback.Detail);
    }

    [Fact]
    public async Task UnreasonableTtlDropTriggersTransactionalRollback()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, remainingTtlSeconds: 117, firstReloadedTtlSeconds: 1);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        LmStudioTemplateApplyException exception = await Assert.ThrowsAsync<LmStudioTemplateApplyException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.True(exception.Rollback.Succeeded, exception.Rollback.Detail);
        Assert.Equal("qwen/root:3", fixture.Handler.CurrentInstanceId);
        Assert.Collection(fixture.Handler.LoadBodies, _ => { }, _ => { });
    }

    [Fact]
    public async Task SameSourceAmbiguityBlocksBeforeJournalOrUnload()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        fixture.Handler.AdditionalInstanceId = "qwen/root:parallel";

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.False(File.Exists(fixture.Store.GetPath(plan.TransactionId)));
    }

    [Fact]
    public async Task RunningCodexBlocksBeforeJournalOrLifecycleRequest()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, codexRunning: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.ApplyTemplateAsync(plan));

        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.False(File.Exists(fixture.Store.GetPath(plan.TransactionId)));
    }

    [Fact]
    public async Task ChangedGgufBlocksCrashRecoveryWithoutLifecycleMutation()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.OriginalUnloaded);
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateNoLoadedInstance();
        await File.AppendAllTextAsync(fixture.GgufPath, "changed");

        IOException blocked = await Assert.ThrowsAsync<IOException>(() => fixture.Controller.RecoverAsync(record));

        Assert.Contains("GGUF", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.Equal(0, fixture.Handler.UnloadCount);
        LmStudioTemplateTransactionRecord saved = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(LmStudioTemplateTransactionState.OriginalUnloaded, saved.State);
    }

    [Fact]
    public async Task CrashRecoveryUnloadsKnownPatchedInstanceThenRestoresBuiltInTemplate()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        const string patchedId = "runtime-patched";
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.PatchedAndVerified) with
        {
            PatchedInstanceId = patchedId,
        };
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateLoadedInstance(patchedId);

        LmStudioRollbackResult recovered = await fixture.Controller.RecoverAsync(record);

        Assert.True(recovered.Succeeded, recovered.Detail);
        Assert.Equal(1, fixture.Handler.UnloadCount);
        Assert.Equal("qwen/root:2", recovered.RestoredInstance?.InstanceId);
        using JsonDocument restored = JsonDocument.Parse(Assert.Single(fixture.Handler.LoadBodies));
        Assert.False(restored.RootElement.TryGetProperty("prompt_template", out _));
    }

    [Fact]
    public async Task LegacyRollbackFailedWithExactOriginalInstanceIsAlreadyRestoredWithoutLifecycleRequests()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord legacy = ControllerFixture.CreateTransactionRecord(
            plan,
            original,
            LmStudioTemplateTransactionState.RollbackFailed);
        await fixture.Store.WriteAsync(legacy);

        LmStudioRecoveryAssessment assessment = await fixture.Controller.AssessRecoveryAsync(legacy);
        LmStudioRollbackResult result = await fixture.Controller.RecoverAsync(legacy, assessment);

        Assert.Equal(LmStudioRecoveryDisposition.AlreadyRestored, assessment.Disposition);
        Assert.True(assessment.IsLegacyJournal);
        Assert.False(assessment.RequiresLifecycleMutation);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        LmStudioTemplateTransactionRecord saved = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(2, saved.SchemaVersion);
        Assert.Equal(LmStudioTemplateTransactionState.RolledBack, saved.State);
        Assert.Equal("qwen/root", saved.LoadModelKey);
    }

    [Fact]
    public async Task RecoveryAssessmentDriftBlocksBeforeAnyUnload()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord legacy = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.RollbackFailed);
        await fixture.Store.WriteAsync(legacy);
        LmStudioRecoveryAssessment assessment = await fixture.Controller.AssessRecoveryAsync(legacy);
        fixture.Handler.CurrentContextLength = 65_536;

        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.RecoverAsync(legacy, assessment));

        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
    }

    [Fact]
    public async Task RecoveryLoadResponseWithoutInstanceIdCanBeReconciledFromUniqueNativeInstance()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.OriginalUnloaded);
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateNoLoadedInstance();
        fixture.Handler.FirstLoadOmitsInstanceId = true;

        LmStudioRollbackResult result = await fixture.Controller.RecoverAsync(record);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("qwen/root:2", result.RestoredInstance?.InstanceId);
        Assert.Single(fixture.Handler.LoadBodies);
        Assert.Equal(0, fixture.Handler.UnloadCount);
    }

    [Fact]
    public async Task RecoveryAttemptIsPersistedBeforeOriginalLoadFailure()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true, firstLoadFails: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.OriginalUnloaded);
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateNoLoadedInstance();

        LmStudioRollbackResult result = await fixture.Controller.RecoverAsync(record);

        Assert.False(result.Succeeded);
        LmStudioTemplateTransactionRecord saved = Assert.IsType<LmStudioTemplateTransactionRecord>(await fixture.Store.ReadAsync(plan.TransactionId));
        Assert.Equal(2, saved.SchemaVersion);
        Assert.Equal(1, saved.RecoveryAttemptCount);
        Assert.Equal(LmStudioLifecycleStage.LoadOriginal, saved.LastRecoveryFailureStage);
    }

    [Fact]
    public async Task KnownPatchWithOriginalInstanceUnloadsOnlyKnownPatchAndAdoptsOriginal()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        const string patchedId = "runtime-patched";
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.PatchedLoaded) with
        {
            PatchedInstanceId = patchedId,
        };
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateLoadedInstance(patchedId);
        fixture.Handler.AdditionalInstanceId = "runtime-ambiguous";

        LmStudioRollbackResult recovered = await fixture.Controller.RecoverAsync(record);

        Assert.True(recovered.Succeeded, recovered.Detail);
        Assert.Equal(1, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.Equal("runtime-ambiguous", recovered.RestoredInstance?.InstanceId);
    }

    [Fact]
    public async Task UnknownMultipleCandidatesStopBeforeUnloadingAnyInstance()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.RollbackFailed);
        await fixture.Store.WriteAsync(record);
        fixture.Handler.SimulateLoadedInstance("runtime-unknown-a");
        fixture.Handler.AdditionalInstanceId = "runtime-unknown-b";

        LmStudioRecoveryAssessment assessment = await fixture.Controller.AssessRecoveryAsync(record);
        LmStudioRollbackResult recovered = await fixture.Controller.RecoverAsync(record, assessment);

        Assert.Equal(LmStudioRecoveryDisposition.BlockedAmbiguous, assessment.Disposition);
        Assert.False(recovered.Succeeded);
        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
        Assert.Equal("runtime-unknown-a", fixture.Handler.CurrentInstanceId);
    }

    [Fact]
    public async Task ExactOriginalPlusUnknownCandidateRemainsAmbiguousWithoutKnownPatchId()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan plan = fixture.CreatePlan(original);
        LmStudioTemplateTransactionRecord record = ControllerFixture.CreateTransactionRecord(plan, original, LmStudioTemplateTransactionState.RollbackFailed);
        await fixture.Store.WriteAsync(record);
        fixture.Handler.AdditionalInstanceId = "runtime-unknown";

        LmStudioRecoveryAssessment assessment = await fixture.Controller.AssessRecoveryAsync(record);
        LmStudioRollbackResult result = await fixture.Controller.RecoverAsync(record, assessment);

        Assert.Equal(LmStudioRecoveryDisposition.BlockedAmbiguous, assessment.Disposition);
        Assert.False(result.Succeeded);
        Assert.Equal(0, fixture.Handler.UnloadCount);
        Assert.Empty(fixture.Handler.LoadBodies);
    }

    [Fact]
    public async Task LifecycleFileLeaseBlocksParallelControllerUntilTransactionEnds()
    {
        using var fixture = new ControllerFixture(hierarchyPasses: true);
        LmStudioLoadedInstanceSnapshot original = await fixture.Controller.CaptureAsync(ControllerFixture.OriginalInstanceId);
        LmStudioTemplateRepairPlan firstPlan = fixture.CreatePlan(original);
        LmStudioTemplateRepairResult applied = await fixture.Controller.ApplyTemplateAsync(firstPlan);
        using LmStudioInstanceController second = fixture.CreateAdditionalController();
        LmStudioTemplateRepairPlan secondPlan = fixture.CreatePlan(original);

        InvalidOperationException blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => second.ApplyTemplateAsync(secondPlan));

        Assert.Contains("另一个 Codex Model Manager", blocked.Message, StringComparison.Ordinal);
        LmStudioRollbackResult rollback = await fixture.Controller.RollbackAsync(firstPlan, applied.PatchedInstance.InstanceId);
        Assert.True(rollback.Succeeded, rollback.Detail);
    }

    private sealed class ControllerFixture : IDisposable
    {
        public const string OriginalInstanceId = "qwen/root";
        public const string LegacyV2Template = "legacy-v2-template";
        private readonly TemporaryDirectory temporary = new();
        private readonly TestCodexHomeProvider home;
        private readonly StubTemplateReader reader;
        private readonly StubTemplateRepair repair;
        private readonly bool codexRunning;

        public ControllerFixture(
            bool hierarchyPasses,
            bool firstLoadOmitsConfig = false,
            bool firstLoadReusesOriginalId = false,
            bool firstLoadFails = false,
            bool secondLoadFails = false,
            bool firstLoadWithoutSuffix = false,
            int? remainingTtlSeconds = null,
            int? firstReloadedTtlSeconds = null,
            bool codexRunning = false)
        {
            this.codexRunning = codexRunning;
            string ggufPath = Path.Combine(temporary.Path, "model.gguf");
            File.WriteAllText(ggufPath, "fixture", new UTF8Encoding(false));
            FileInfo file = new(ggufPath);
            GgufPath = ggufPath;
            Analysis = new GgufChatTemplateAnalysis(
                ggufPath,
                file.Name,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc),
                3,
                "Qwen Fixture",
                "qwen35",
                "original-template",
                Hash("original-template"));
            Preview = new PromptTemplateRepairPreview(
                PromptTemplateRepairStatus.Supported,
                "test",
                "patched-template",
                Hash("patched-template"),
                PromptTemplateRepairService.RuleVersion);
            reader = new StubTemplateReader(Analysis);
            repair = new StubTemplateRepair(Preview);
            Handler = new LifecycleHandler(
                hierarchyPasses,
                firstLoadOmitsConfig,
                firstLoadReusesOriginalId,
                firstLoadFails,
                secondLoadFails,
                firstLoadWithoutSuffix,
                remainingTtlSeconds,
                firstReloadedTtlSeconds);
            var paths = new AppPaths(Path.Combine(temporary.Path, "local"));
            Store = new LmStudioTemplateTransactionStore(paths);
            home = new TestCodexHomeProvider(Path.Combine(temporary.Path, "codex-home"));
            Controller = CreateAdditionalController();
        }

        public LmStudioInstanceController CreateAdditionalController() => new(
                new Uri("http://127.0.0.1:1234"),
                false,
                new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(10) },
                null,
                new FakeRuntimeProbe(home, running: codexRunning),
                reader,
                repair,
                Store,
                new FakeLogger());

        public string GgufPath { get; }
        public GgufChatTemplateAnalysis Analysis { get; }
        public PromptTemplateRepairPreview Preview { get; }
        public LifecycleHandler Handler { get; }
        public LmStudioTemplateTransactionStore Store { get; }
        public LmStudioInstanceController Controller { get; }

        public LmStudioTemplateRepairPlan CreatePlan(LmStudioLoadedInstanceSnapshot original) => new(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
            original,
            new LmStudioModelFileResolution(GgufPath, "qwen/root", "qwen/root@q8_0", "qwen35", "Q8_0", "test"),
            Analysis,
            Preview,
            new LmStudioRuntimeTemplateProvenance(LmStudioRuntimeTemplateMode.BuiltIn),
            FakeLmStudioSwitchPreflight.Fail());

        public async Task<LmStudioTemplateRepairPlan> CreateV2UpgradePlanAsync(LmStudioLoadedInstanceSnapshot original)
        {
            Guid evidenceId = Guid.NewGuid();
            string v2Sha = HashTemplate(LegacyV2Template);
            var evidence = new LmStudioTemplateTransactionRecord(
                2,
                evidenceId,
                LmStudioTemplateTransactionState.Completed,
                DateTimeOffset.Now.AddMinutes(-2),
                DateTimeOffset.Now.AddMinutes(-1),
                original,
                CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder,
                Analysis.FilePath,
                Analysis.FileName,
                Analysis.FileLength,
                Analysis.LastWriteTimeUtc,
                Analysis.GgufVersion,
                Analysis.TemplateSha256,
                v2Sha,
                PromptTemplateRepairService.LegacyLeadingRuleVersion,
                original.InstanceId,
                "legacy v2 completed evidence",
                LoadModelKey: original.SourceModelKey,
                LastStableState: LmStudioTemplateTransactionState.Completed);
            await Store.WriteAsync(evidence);
            Handler.SetRuntimeTemplate(LegacyV2Template);
            return CreatePlan(original) with
            {
                FailureCode = CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder,
                OriginalRuntimeTemplate = new LmStudioRuntimeTemplateProvenance(
                    LmStudioRuntimeTemplateMode.ManagerRule,
                    PromptTemplateRepairService.LegacyLeadingRuleVersion,
                    v2Sha,
                    evidenceId),
                OriginalHierarchyProbe = V2FailureProbe(),
                OriginalRuntimeTemplateText = LegacyV2Template,
            };
        }

        public static CodexInstructionHierarchyProbeResult V2FailureProbe() => new(
            new CodexInstructionProbeStepResult(true, 200),
            new CodexInstructionProbeStepResult(true, 200),
            new CodexInstructionProbeStepResult(true, 200),
            new CodexInstructionProbeStepResult(false, 500),
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder,
            "legacy v2 continuation failure",
            DateTimeOffset.Now);

        public static string HashTemplate(string value) => Hash(value);

        public static LmStudioTemplateTransactionRecord CreateTransactionRecord(
            LmStudioTemplateRepairPlan plan,
            LmStudioLoadedInstanceSnapshot original,
            LmStudioTemplateTransactionState state) => new(
                1,
                plan.TransactionId,
                state,
                plan.CreatedAt,
                DateTimeOffset.Now,
                original,
                plan.FailureCode,
                plan.GgufAnalysis.FilePath,
                plan.GgufAnalysis.FileName,
                plan.GgufAnalysis.FileLength,
                plan.GgufAnalysis.LastWriteTimeUtc,
                plan.GgufAnalysis.GgufVersion,
                plan.GgufAnalysis.TemplateSha256,
                plan.TemplatePreview.PatchedTemplateSha256!,
                plan.TemplatePreview.RuleVersion,
                null,
                "test");

        public void Dispose()
        {
            Controller.Dispose();
            temporary.Dispose();
        }

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed class LifecycleHandler(
        bool hierarchyPasses,
        bool firstLoadOmitsConfig,
        bool firstLoadReusesOriginalId,
        bool firstLoadFails,
        bool secondLoadFails,
        bool firstLoadWithoutSuffix,
        int? remainingTtlSeconds,
        int? firstReloadedTtlSeconds) : HttpMessageHandler
    {
        private int loadCount;

        public string? CurrentInstanceId { get; private set; } = ControllerFixture.OriginalInstanceId;
        public bool CurrentHasPatchedTemplate { get; private set; }
        public string? CurrentRuntimeTemplate { get; private set; }
        public int CurrentContextLength { get; set; } = 32_768;
        public int UnloadCount { get; private set; }
        public List<string> LoadBodies { get; } = [];
        public string? AdditionalInstanceId { get; set; }
        public int? CurrentRemainingTtlSeconds { get; private set; } = remainingTtlSeconds;
        public string CurrentSelectedVariant { get; set; } = "qwen/root@q8_0";
        public string? SelectedVariantAfterNextUnload { get; set; }
        public bool FirstLoadOmitsInstanceId { get; set; }

        public void SimulateNoLoadedInstance()
        {
            CurrentInstanceId = null;
            CurrentHasPatchedTemplate = false;
            CurrentRuntimeTemplate = null;
        }

        public void SimulateLoadedInstance(string instanceId, bool patchedTemplate = true)
        {
            CurrentInstanceId = instanceId;
            CurrentHasPatchedTemplate = patchedTemplate;
            CurrentRuntimeTemplate = patchedTemplate ? "patched-template" : null;
        }

        public void SetRuntimeTemplate(string? template)
        {
            CurrentRuntimeTemplate = template;
            CurrentHasPatchedTemplate = template is not null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/api/v1/models", StringComparison.Ordinal))
            {
                return StubHttpHandler.Json(BuildModels());
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/models/unload", StringComparison.Ordinal))
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                string id = body.RootElement.GetProperty("instance_id").GetString()!;
                if (!string.Equals(id, CurrentInstanceId, StringComparison.Ordinal))
                {
                    return StubHttpHandler.Json("{\"error\":\"wrong instance\"}", HttpStatusCode.BadRequest);
                }

                CurrentInstanceId = null;
                CurrentHasPatchedTemplate = false;
                CurrentRuntimeTemplate = null;
                if (!string.IsNullOrWhiteSpace(SelectedVariantAfterNextUnload))
                {
                    CurrentSelectedVariant = SelectedVariantAfterNextUnload;
                    SelectedVariantAfterNextUnload = null;
                }
                UnloadCount++;
                return StubHttpHandler.Json("{}");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/models/load", StringComparison.Ordinal))
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using JsonDocument loadRequest = JsonDocument.Parse(body);
                string requestedModel = loadRequest.RootElement.GetProperty("model").GetString()!;
                if (requestedModel.StartsWith("__cmm_schema_probe_", StringComparison.Ordinal))
                {
                    return StubHttpHandler.Json(
                        JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                type = "model_not_found",
                                message = $"Model {requestedModel} not found in downloaded models",
                            },
                        }),
                        HttpStatusCode.NotFound);
                }

                LoadBodies.Add(body);
                int? requestedTtl = loadRequest.RootElement.TryGetProperty("ttl_seconds", out JsonElement ttlElement) && ttlElement.TryGetInt32(out int ttl)
                    ? ttl
                    : null;
                loadCount++;
                if (loadCount == 1 && firstLoadFails || loadCount == 2 && secondLoadFails)
                {
                    CurrentInstanceId = null;
                    CurrentHasPatchedTemplate = false;
                    CurrentRuntimeTemplate = null;
                    return StubHttpHandler.Json("{\"error\":\"simulated load failure\"}", HttpStatusCode.InternalServerError);
                }

                CurrentHasPatchedTemplate = loadRequest.RootElement.TryGetProperty("prompt_template", out JsonElement promptTemplate) &&
                    promptTemplate.ValueKind == JsonValueKind.Object;
                CurrentRuntimeTemplate = CurrentHasPatchedTemplate
                    ? promptTemplate.GetProperty("template").GetString()
                    : null;
                CurrentInstanceId = loadCount == 1
                    ? firstLoadReusesOriginalId ? ControllerFixture.OriginalInstanceId : firstLoadWithoutSuffix ? "runtime-patched" : "qwen/root:2"
                    : "qwen/root:3";
                CurrentRemainingTtlSeconds = loadCount == 1 && firstReloadedTtlSeconds is not null
                    ? firstReloadedTtlSeconds
                    : requestedTtl;
                if (loadCount == 1 && FirstLoadOmitsInstanceId)
                {
                    return StubHttpHandler.Json($$"""
                        {"status":"loaded","load_config":{{BuildConfigJson()}}}
                        """);
                }

                if (loadCount == 1 && firstLoadOmitsConfig)
                {
                    return StubHttpHandler.Json($$"""
                        {"instance_id":"{{CurrentInstanceId}}","status":"loaded"}
                        """);
                }

                return StubHttpHandler.Json($$"""
                    {"instance_id":"{{CurrentInstanceId}}","status":"loaded","load_config":{{BuildConfigJson()}}}
                    """);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/v1/responses", StringComparison.Ordinal))
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using JsonDocument responseRequest = JsonDocument.Parse(body);
                string[] roles = responseRequest.RootElement.GetProperty("input").EnumerateArray()
                    .Select(item => item.GetProperty("role").GetString() ?? string.Empty)
                    .ToArray();
                bool developerProbe = roles.Contains("developer", StringComparer.Ordinal);
                bool seenConversation = false;
                bool continuationDeveloper = false;
                foreach (string role in roles)
                {
                    if (role is "system" or "developer")
                    {
                        continuationDeveloper |= seenConversation;
                    }
                    else
                    {
                        seenConversation = true;
                    }
                }

                if (CurrentRuntimeTemplate == ControllerFixture.LegacyV2Template && continuationDeveloper)
                {
                    return StubHttpHandler.Json("{\"error\":\"System and developer messages must precede conversation messages.\"}", HttpStatusCode.InternalServerError);
                }

                if (developerProbe && (CurrentRuntimeTemplate is null || CurrentRuntimeTemplate != ControllerFixture.LegacyV2Template && !hierarchyPasses))
                {
                    return StubHttpHandler.Json("{\"error\":\"System message must be at the beginning\"}", HttpStatusCode.InternalServerError);
                }

                return StubHttpHandler.Json("{\"output\":[]}");
            }

            return StubHttpHandler.Json("{}", HttpStatusCode.NotFound);
        }

        private string BuildModels()
        {
            List<string> instances = [];
            if (CurrentInstanceId is not null)
            {
                instances.Add(BuildInstanceJson(CurrentInstanceId));
            }

            if (AdditionalInstanceId is not null)
            {
                instances.Add(BuildInstanceJson(AdditionalInstanceId));
            }

            return $$"""
                {"models":[{"key":"qwen/root","display_name":"Qwen","selected_variant":"{{CurrentSelectedVariant}}","variants":["qwen/root@q4_k_m","qwen/root@q8_0"],"type":"llm","format":"gguf","architecture":"qwen35","quantization":{"name":"{{(CurrentSelectedVariant.EndsWith("@q8_0", StringComparison.Ordinal) ? "Q8_0" : "Q4_K_M")}}"},"params_string":"27B","max_context_length":262144,"loaded_instances":[{{string.Join(',', instances)}}]}]}
                """;
        }

        private string BuildConfigJson() => $$"""
            {"context_length":{{CurrentContextLength}},"eval_batch_size":4096,"physical_batch_size":512,"parallel":2,"flash_attention":true,"context_checkpoints":8,"reasoning_budget_message":"","speculative_draft_mtp":true,"speculative_draft_simple":false,"speculative_draft_model":"","speculative_draft_max_tokens":2,"speculative_draft_min_tokens":0,"speculative_draft_min_continue_probability":0.75,"offload_kv_cache_to_gpu":false}
            """;

        private string BuildInstanceJson(string instanceId)
        {
            string ttl = CurrentRemainingTtlSeconds is > 0 ? $",\"remaining_ttl_seconds\":{CurrentRemainingTtlSeconds.Value}" : string.Empty;
            return $"{{\"id\":{JsonSerializer.Serialize(instanceId)},\"config\":{BuildConfigJson()}{ttl}}}";
        }
    }

    private sealed class StubTemplateReader(GgufChatTemplateAnalysis analysis) : IGgufChatTemplateReader
    {
        public Task<GgufChatTemplateAnalysis> ReadAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(analysis);
    }

    private sealed class StubTemplateRepair(PromptTemplateRepairPreview preview) : IPromptTemplateRepairService
    {
        public PromptTemplateRepairPreview CreatePreview(GgufChatTemplateAnalysis analysis) => preview;

        public string RecreateKnownTemplate(GgufChatTemplateAnalysis analysis, string ruleVersion, string expectedTemplateSha256)
        {
            string template = ruleVersion == PromptTemplateRepairService.LegacyLeadingRuleVersion
                ? ControllerFixture.LegacyV2Template
                : preview.PatchedTemplate ?? throw new InvalidDataException("test template unavailable");
            if (!ControllerFixture.HashTemplate(template).Equals(expectedTemplateSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("test deterministic template hash mismatch");
            }

            return template;
        }

        public Task<PromptTemplateRepairArtifact> ExportAsync(GgufChatTemplateAnalysis analysis, string modelId, string outputRoot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
