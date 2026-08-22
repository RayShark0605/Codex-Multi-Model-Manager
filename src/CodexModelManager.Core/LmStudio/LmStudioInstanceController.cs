using System.Security.Cryptography;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Core.LmStudio;

public sealed class LmStudioInstanceController : ILmStudioInstanceController
{
    private readonly LmStudioClient client;
    private readonly Uri endpoint;
    private readonly bool requiresAuthentication;
    private readonly HttpClient httpClient;
    private readonly Func<string?>? tokenProvider;
    private readonly ICodexRuntimeProbe runtimeProbe;
    private readonly IGgufChatTemplateReader ggufReader;
    private readonly IPromptTemplateRepairService templateRepair;
    private readonly LmStudioTemplateTransactionStore transactions;
    private readonly IAppLogger logger;
    private FileStream? lifecycleLease;
    private Guid? lifecycleLeaseTransactionId;

    public LmStudioInstanceController(
        Uri endpoint,
        bool requiresAuthentication,
        HttpClient httpClient,
        Func<string?>? tokenProvider,
        ICodexRuntimeProbe runtimeProbe,
        IGgufChatTemplateReader ggufReader,
        IPromptTemplateRepairService templateRepair,
        LmStudioTemplateTransactionStore transactions,
        IAppLogger logger)
    {
        LmStudioEndpointPolicy.Validate(endpoint);
        this.endpoint = endpoint;
        this.requiresAuthentication = requiresAuthentication;
        this.httpClient = httpClient;
        this.tokenProvider = requiresAuthentication ? tokenProvider : null;
        this.runtimeProbe = runtimeProbe;
        this.ggufReader = ggufReader;
        this.templateRepair = templateRepair;
        this.transactions = transactions;
        this.logger = logger;
        client = new LmStudioClient(endpoint, this.tokenProvider, httpClient);
    }

    public event EventHandler<string>? ProgressChanged;

    public void Dispose()
    {
        lifecycleLease?.Dispose();
        lifecycleLease = null;
        lifecycleLeaseTransactionId = null;
        GC.SuppressFinalize(this);
    }

    public async Task<LmStudioLoadedInstanceSnapshot> CaptureAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile[] matches = models.Where(model => model.IsLoaded == true && model.Id.Equals(instanceId, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(matches.Length == 0
                ? $"LM Studio native API 未报告 loaded instance: {instanceId}"
                : $"LM Studio native API 返回重复 instance ID: {instanceId}");
        }

        string? sourceModelKey = matches[0].SourceModelKey;
        int sameSourceCount = models.Count(model =>
            model.IsLoaded == true &&
            !string.IsNullOrWhiteSpace(sourceModelKey) &&
            string.Equals(model.SourceModelKey, sourceModelKey, StringComparison.OrdinalIgnoreCase));
        if (sameSourceCount != 1)
        {
            throw new InvalidOperationException("同一源模型存在多个 loaded instance；无法唯一证明待修复实例，自动生命周期操作已阻断。");
        }

        return CreateSnapshot(matches[0]);
    }

    public async Task<LmStudioTemplateRepairResult> ApplyTemplateAsync(
        LmStudioTemplateRepairPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureControllerMatches(plan.OriginalInstance);
        AcquireLifecycleLease(plan.TransactionId);
        bool retainLease = false;
        try
        {
            ReportProgress("应用前校验：Codex 进程、实例、配置与 GGUF 指纹");
            await EnsureCodexClosedAsync(cancellationToken).ConfigureAwait(false);
            LmStudioLoadedInstanceSnapshot current = await ValidatePlanBeforeMutationAsync(plan, cancellationToken).ConfigureAwait(false);
            plan = plan with { OriginalInstance = current };

            ReportProgress("写入 Prepared 恢复事务");
            LmStudioTemplateTransactionRecord record = CreateRecord(plan);
            await transactions.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            logger.Info($"LM Studio template transaction prepared: id={plan.TransactionId:N}, instance={plan.OriginalInstance.InstanceId}, variant={plan.OriginalInstance.SelectedVariant ?? plan.OriginalInstance.SourceModelKey}, originalSha={ShortHash(plan.GgufAnalysis.TemplateSha256)}, patchedSha={ShortHash(plan.TemplatePreview.PatchedTemplateSha256!)}");

            string? patchedInstanceId = null;
            LmStudioLifecycleStage failureStage = LmStudioLifecycleStage.UnloadOriginal;
            try
            {
                ReportProgress("卸载原始 LM Studio 实例");
                await client.UnloadAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false);
                if (await TryCaptureAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidOperationException("LM Studio unload 返回成功，但原 instance 仍存在；已停止加载补丁实例。");
                }

                await EnsureSourceAbsentAsync(plan.OriginalInstance.SourceModelKey, cancellationToken).ConfigureAwait(false);

                record = await UpdateRecordAsync(record, LmStudioTemplateTransactionState.OriginalUnloaded, null, "原实例已卸载。", cancellationToken).ConfigureAwait(false);
                ReportProgress("加载带运行时 Prompt Template 的精确模型变体");
                failureStage = LmStudioLifecycleStage.LoadPatched;
                await ValidateLoadTargetCurrentAsync(plan.OriginalInstance, cancellationToken).ConfigureAwait(false);
                string loadModel = plan.OriginalInstance.SourceModelKey;
                var promptTemplate = new LmStudioPromptTemplateConfiguration("jinja", plan.TemplatePreview.PatchedTemplate!, []);
                LmStudioLoadResponse load = await client.LoadAsync(
                    loadModel,
                    plan.OriginalInstance.LoadConfiguration,
                    promptTemplate,
                    PositiveTtl(plan.OriginalInstance.RemainingTtlSeconds),
                    cancellationToken).ConfigureAwait(false);
                patchedInstanceId = load.InstanceId;
                if (!LmStudioClient.LoadConfigurationsEqual(plan.OriginalInstance.LoadConfiguration, load.EchoedConfiguration))
                {
                    throw new InvalidDataException("LM Studio load_config 回显与原实例配置不一致。");
                }

                ReportProgress("验证新实例 ID、量化、上下文与完整加载配置");
                failureStage = LmStudioLifecycleStage.ValidatePatched;
                LmStudioLoadedInstanceSnapshot patched = await CaptureAsync(patchedInstanceId, cancellationToken).ConfigureAwait(false);
                ValidateReloadedSnapshot(plan.OriginalInstance, patched);
                record = await UpdateRecordAsync(record, LmStudioTemplateTransactionState.PatchedLoaded, patchedInstanceId, "补丁实例已加载并验证配置。", cancellationToken).ConfigureAwait(false);

                ReportProgress("验证四阶段 Codex 指令层级（含多轮后置 developer）");
                failureStage = LmStudioLifecycleStage.ProbePatched;
                var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
                CodexInstructionHierarchyProbeResult hierarchy = await probe.ProbeAsync(patchedInstanceId, cancellationToken).ConfigureAwait(false);
                if (!hierarchy.IsCompatible)
                {
                    throw new LmStudioCompatibilityException(hierarchy);
                }

                // The hierarchy request is the authoritative proof that the runtime
                // template took effect. Re-read native state once more afterwards so
                // a concurrent unload/reload cannot be reported as a verified result.
                patched = await CaptureAsync(patchedInstanceId, cancellationToken).ConfigureAwait(false);
                ValidateReloadedSnapshot(plan.OriginalInstance, patched);
                record = await UpdateRecordAsync(record, LmStudioTemplateTransactionState.PatchedAndVerified, patchedInstanceId, "补丁实例 Codex 指令层级 PASS。", cancellationToken).ConfigureAwait(false);
                logger.Info($"LM Studio template transaction verified: id={plan.TransactionId:N}, instance={patchedInstanceId}, control={hierarchy.Control.HttpStatus}, leading={hierarchy.LeadingDeveloper.HttpStatus}, conversation={hierarchy.ConversationControl.HttpStatus}, continuation={hierarchy.ContinuationDeveloper.HttpStatus}");
                ReportProgress("PatchedAndVerified：等待 Codex 配置最终确认");
                retainLease = true;
                return new LmStudioTemplateRepairResult(plan, patched, hierarchy, transactions.GetPath(plan.TransactionId));
            }
            catch (Exception exception)
            {
                record = await RecordApplyFailureEvidenceAsync(record, plan.OriginalInstance.SourceModelKey, patchedInstanceId, failureStage, exception).ConfigureAwait(false);
                patchedInstanceId ??= record.CandidateInstanceId;
                ReportProgress(plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule
                    ? $"修复失败：正在事务式恢复原运行时规则 {plan.OriginalRuntimeTemplate.RuleVersion}"
                    : "修复失败：正在事务式恢复原始内置模板");
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                LmStudioRollbackResult rollback = await RollbackCoreAsync(
                    plan.OriginalInstance,
                    plan.TransactionId,
                    patchedInstanceId,
                    record,
                    rollbackTimeout.Token).ConfigureAwait(false);
                throw new LmStudioTemplateApplyException(
                    $"LM Studio 运行时 Prompt Template 修复失败。{rollback.Detail}",
                    exception,
                    rollback,
                    plan,
                    failureStage);
            }
        }
        finally
        {
            if (!retainLease)
            {
                ReleaseLifecycleLease(plan.TransactionId);
            }
        }
    }

    public async Task<LmStudioRollbackResult> RollbackAsync(
        LmStudioTemplateRepairPlan plan,
        string? patchedInstanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureControllerMatches(plan.OriginalInstance);
        AcquireLifecycleLease(plan.TransactionId);
        try
        {
            LmStudioTemplateTransactionRecord record = await transactions.ReadAsync(plan.TransactionId, cancellationToken).ConfigureAwait(false)
                ?? CreateRecord(plan);
            return await RollbackCoreAsync(plan.OriginalInstance, plan.TransactionId, patchedInstanceId, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLifecycleLease(plan.TransactionId);
        }
    }

    public async Task<LmStudioRollbackResult> RecoverAsync(
        LmStudioTemplateTransactionRecord transaction,
        CancellationToken cancellationToken = default)
    {
        LmStudioRecoveryAssessment assessment = await AssessRecoveryAsync(transaction, cancellationToken).ConfigureAwait(false);
        return await RecoverAsync(transaction, assessment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LmStudioRecoveryAssessment> AssessRecoveryAsync(
        LmStudioTemplateTransactionRecord transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnsureControllerMatches(transaction.OriginalInstance);
        GgufChatTemplateAnalysis recoveryAnalysis = await ValidateJournalGgufAsync(transaction, cancellationToken).ConfigureAwait(false);
        await ValidateOriginalRuntimeEvidenceAsync(transaction, recoveryAnalysis, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile[] sameSource = models.Where(model =>
            model.IsLoaded == true &&
            string.Equals(model.SourceModelKey, transaction.OriginalInstance.SourceModelKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        List<LmStudioRecoveryCandidate> candidates = [];
        var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
        foreach (ModelProfile model in sameSource.OrderBy(model => model.Id, StringComparer.Ordinal))
        {
            LmStudioLoadedInstanceSnapshot snapshot = CreateSnapshot(model);
            bool matchesOriginal = SnapshotsReloadEquivalent(transaction.OriginalInstance, snapshot);
            CodexInstructionHierarchyProbeResult? hierarchy = matchesOriginal
                ? await probe.ProbeAsync(snapshot.InstanceId, cancellationToken).ConfigureAwait(false)
                : null;
            bool reproducesOriginal = hierarchy is not null && ReproducesOriginalRuntimeSignature(hierarchy, transaction);
            candidates.Add(new LmStudioRecoveryCandidate(snapshot, matchesOriginal, hierarchy, reproducesOriginal));
        }

        string? knownPatchId = transaction.PatchedInstanceId ?? transaction.CandidateInstanceId;
        bool knownPatchPresent = !string.IsNullOrWhiteSpace(knownPatchId) &&
            candidates.Any(candidate => candidate.Snapshot.InstanceId.Equals(knownPatchId, StringComparison.Ordinal));
        LmStudioRecoveryCandidate? alreadyRestored = candidates.Count == 1 ? candidates[0] : null;
        bool originalStageEvidence = transaction.FailureStage is LmStudioLifecycleStage.LoadOriginal or LmStudioLifecycleStage.ValidateOriginal or LmStudioLifecycleStage.ProbeOriginal;
        bool canCloseAsAlreadyRestored = alreadyRestored is not null &&
            alreadyRestored.MatchesOriginalSnapshot &&
            alreadyRestored.ReproducesOriginalFailure &&
            (alreadyRestored.Snapshot.InstanceId.Equals(transaction.OriginalInstance.InstanceId, StringComparison.Ordinal) || originalStageEvidence);
        LmStudioRecoveryDisposition disposition;
        string? instanceToUnload = null;
        string detail;
        if (canCloseAsAlreadyRestored)
        {
            disposition = LmStudioRecoveryDisposition.AlreadyRestored;
            detail = transaction.SchemaVersion < 3
                ? "Legacy 事务缺少 v3 provenance，但当前唯一实例的 ID、完整配置和原始层级失败签名均与快照一致；可只关闭 journal，不重载模型。"
                : "当前唯一实例的完整配置和原始运行时模板四阶段签名均与事务快照一致；可只关闭 journal，不重载模型。";
        }
        else if (candidates.Count == 0)
        {
            disposition = LmStudioRecoveryDisposition.LoadOriginal;
            detail = transaction.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.ManagerRule
                ? $"当前没有 {transaction.OriginalInstance.SourceModelKey} 的 loaded instance；将使用源模型 key 和已验证的 {transaction.OriginalRuntimeRuleVersion} 模板恢复。"
                : $"当前没有 {transaction.OriginalInstance.SourceModelKey} 的 loaded instance；将使用源模型 key 不带模板恢复。";
        }
        else if (knownPatchPresent)
        {
            disposition = LmStudioRecoveryDisposition.UnloadKnownPatchAndLoadOriginal;
            instanceToUnload = knownPatchId;
            detail = transaction.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.ManagerRule
                ? $"已由事务记录唯一识别补丁实例 {knownPatchId}；将只卸载该实例，再恢复并验证 {transaction.OriginalRuntimeRuleVersion}。"
                : $"已由事务记录唯一识别补丁实例 {knownPatchId}；将只卸载该实例，再验证或恢复原始内置模板。";
        }
        else
        {
            bool patchStageEvidence = transaction.State is LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified ||
                transaction.LastStableState is LmStudioTemplateTransactionState.OriginalUnloaded or LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified;
            LmStudioRecoveryCandidate? soleCompatiblePatch = candidates.Count == 1 &&
                patchStageEvidence &&
                candidates[0].HierarchyProbe?.IsCompatible == true
                    ? candidates[0]
                    : null;
            if (soleCompatiblePatch is not null)
            {
                disposition = LmStudioRecoveryDisposition.UnloadKnownPatchAndLoadOriginal;
                instanceToUnload = soleCompatiblePatch.Snapshot.InstanceId;
                detail = transaction.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.ManagerRule
                    ? $"事务阶段与层级 PASS 唯一指向补丁实例 {instanceToUnload}；将卸载后恢复 {transaction.OriginalRuntimeRuleVersion}。"
                    : $"事务阶段与层级 PASS 唯一指向补丁实例 {instanceToUnload}；将卸载后恢复原始内置模板。";
            }
            else
            {
                disposition = LmStudioRecoveryDisposition.BlockedAmbiguous;
                detail = $"当前存在 {candidates.Count} 个同源 loaded instance，但没有足够证据唯一识别原始或补丁实例；未计划任何 unload/load。";
            }
        }

        string fingerprint = ComputeRecoveryAssessmentFingerprint(transaction, candidates, disposition, instanceToUnload);
        return new LmStudioRecoveryAssessment(
            transaction.TransactionId,
            disposition,
            candidates,
            instanceToUnload,
            disposition is LmStudioRecoveryDisposition.LoadOriginal or LmStudioRecoveryDisposition.UnloadKnownPatchAndLoadOriginal,
            transaction.SchemaVersion < 3,
            fingerprint,
            detail);
    }

    public async Task<LmStudioRollbackResult> RecoverAsync(
        LmStudioTemplateTransactionRecord transaction,
        LmStudioRecoveryAssessment assessment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(assessment);
        EnsureControllerMatches(transaction.OriginalInstance);
        if (transaction.TransactionId != assessment.TransactionId)
        {
            throw new InvalidOperationException("恢复评估与事务 ID 不一致。");
        }

        if (transaction.State is LmStudioTemplateTransactionState.Completed or LmStudioTemplateTransactionState.RolledBack)
        {
            return new LmStudioRollbackResult(true, "事务已经完成或回滚，无需恢复。", await TryCaptureAsync(transaction.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false), transactions.GetPath(transaction.TransactionId));
        }

        AcquireLifecycleLease(transaction.TransactionId);
        try
        {
            await EnsureCodexClosedAsync(cancellationToken).ConfigureAwait(false);
            LmStudioTemplateTransactionRecord currentRecord = await transactions.ReadAsync(transaction.TransactionId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("LM Studio 模板事务记录不存在。", transactions.GetPath(transaction.TransactionId));
            LmStudioRecoveryAssessment currentAssessment = await AssessRecoveryAsync(currentRecord, cancellationToken).ConfigureAwait(false);
            if (!currentAssessment.StateFingerprint.Equals(assessment.StateFingerprint, StringComparison.Ordinal) ||
                currentAssessment.Disposition != assessment.Disposition ||
                !string.Equals(currentAssessment.InstanceToUnload, assessment.InstanceToUnload, StringComparison.Ordinal))
            {
                throw new IOException("LM Studio 恢复评估后实例、配置或层级状态发生变化；未执行 unload/load，请重新检查恢复状态。");
            }

            int attempt = currentRecord.RecoveryAttemptCount + 1;
            if (currentAssessment.Disposition == LmStudioRecoveryDisposition.BlockedAmbiguous)
            {
                return new LmStudioRollbackResult(false, currentAssessment.Detail, null, transactions.GetPath(transaction.TransactionId));
            }

            if (currentAssessment.Disposition == LmStudioRecoveryDisposition.AlreadyRestored)
            {
                LmStudioLoadedInstanceSnapshot restored = currentAssessment.Candidates.Single(candidate => candidate.ReproducesOriginalFailure).Snapshot;
                await UpdateRecordAsync(
                    currentRecord,
                    LmStudioTemplateTransactionState.RolledBack,
                    null,
                    "已验证当前实例与原始快照及失败签名一致；未执行 unload/load。",
                    cancellationToken,
                    LmStudioLifecycleStage.RecoveryCommit,
                    recoveryAttemptCount: attempt).ConfigureAwait(false);
                return new LmStudioRollbackResult(true, "已验证原始实例状态并关闭恢复事务；未执行 unload/load。", restored, transactions.GetPath(transaction.TransactionId));
            }

            currentRecord = currentRecord with
            {
                SchemaVersion = currentRecord.SchemaVersion,
                UpdatedAt = DateTimeOffset.Now,
                LoadModelKey = currentRecord.LoadModelKey ?? currentRecord.OriginalInstance.SourceModelKey,
                RecoveryAttemptCount = attempt,
            };
            await transactions.WriteAsync(currentRecord, cancellationToken).ConfigureAwait(false);
            return await RollbackCoreAsync(
                currentRecord.OriginalInstance,
                currentRecord.TransactionId,
                currentAssessment.InstanceToUnload,
                currentRecord,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLifecycleLease(transaction.TransactionId);
        }
    }

    public async Task CompleteAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        AcquireLifecycleLease(transactionId);
        try
        {
            LmStudioTemplateTransactionRecord record = await transactions.ReadAsync(transactionId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("LM Studio 模板事务记录不存在。", transactions.GetPath(transactionId));
            if (record.State != LmStudioTemplateTransactionState.PatchedAndVerified)
            {
                throw new InvalidOperationException($"只有 PatchedAndVerified 事务可以完成，当前状态为 {record.State}。");
            }

            await UpdateRecordAsync(record, LmStudioTemplateTransactionState.Completed, record.PatchedInstanceId, "Codex 配置切换已提交；保留补丁实例。", cancellationToken).ConfigureAwait(false);
            logger.Info($"LM Studio template transaction completed: id={transactionId:N}, instance={record.PatchedInstanceId ?? "unknown"}");
            ReportProgress("Completed：Codex 配置已提交，保留补丁实例");
        }
        finally
        {
            ReleaseLifecycleLease(transactionId);
        }
    }

    private async Task<LmStudioRollbackResult> RollbackCoreAsync(
        LmStudioLoadedInstanceSnapshot original,
        Guid transactionId,
        string? patchedInstanceId,
        LmStudioTemplateTransactionRecord record,
        CancellationToken cancellationToken)
    {
        string transactionPath = transactions.GetPath(transactionId);
        LmStudioLifecycleStage failureStage = LmStudioLifecycleStage.RecoveryCommit;
        string? effectivePatchedInstanceId = patchedInstanceId ?? record.PatchedInstanceId ?? record.CandidateInstanceId;
        bool knownPatchWasUnloaded = false;
        try
        {
            await EnsureCodexClosedAsync(cancellationToken).ConfigureAwait(false);
            ReportProgress("回滚校验：GGUF 指纹与当前权威实例状态");
            GgufChatTemplateAnalysis rollbackAnalysis = await ValidateJournalGgufAsync(record, cancellationToken).ConfigureAwait(false);
            await ValidateOriginalRuntimeEvidenceAsync(record, rollbackAnalysis, cancellationToken).ConfigureAwait(false);
            LmStudioPromptTemplateConfiguration? originalPromptTemplate = RecreateOriginalRuntimeTemplate(record, rollbackAnalysis);
            IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(effectivePatchedInstanceId))
            {
                ModelProfile? patched = models.FirstOrDefault(model => model.IsLoaded == true && model.Id.Equals(effectivePatchedInstanceId, StringComparison.Ordinal));
                if (patched is not null)
                {
                    if (!string.Equals(patched.SourceModelKey, original.SourceModelKey, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("事务记录中的补丁 instance ID 当前属于另一个源模型；未执行 unload。");
                    }

                    failureStage = LmStudioLifecycleStage.UnloadPatched;
                    ReportProgress($"卸载补丁实例 {effectivePatchedInstanceId}");
                    await client.UnloadAsync(effectivePatchedInstanceId, cancellationToken).ConfigureAwait(false);
                    knownPatchWasUnloaded = true;
                    models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
                    if (models.Any(model => model.IsLoaded == true && model.Id.Equals(effectivePatchedInstanceId, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException("补丁实例卸载后仍存在。");
                    }
                }
            }

            ModelProfile[] sameSource = models.Where(model =>
                model.IsLoaded == true &&
                string.Equals(model.SourceModelKey, original.SourceModelKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameSource.Length > 1)
            {
                throw new InvalidOperationException("同一源模型存在多个无法唯一归因的 loaded instance；当前恢复状态有歧义，未执行进一步 unload/load。");
            }

            if (sameSource.Length == 1)
            {
                LmStudioLoadedInstanceSnapshot candidate = CreateSnapshot(sameSource[0]);
                bool matchesOriginal = SnapshotsReloadEquivalent(original, candidate);
                bool canAdoptExisting = record.State == LmStudioTemplateTransactionState.Prepared ||
                    record.LastStableState == LmStudioTemplateTransactionState.Prepared ||
                    knownPatchWasUnloaded ||
                    record.FailureStage is LmStudioLifecycleStage.LoadOriginal or LmStudioLifecycleStage.ValidateOriginal or LmStudioLifecycleStage.ProbeOriginal;
                if (matchesOriginal && canAdoptExisting && await ReproducesOriginalRuntimeAsync(candidate.InstanceId, record, cancellationToken).ConfigureAwait(false))
                {
                    await UpdateRecordAsync(record, LmStudioTemplateTransactionState.RolledBack, null, "检测到已恢复的原实例。", cancellationToken).ConfigureAwait(false);
                    return new LmStudioRollbackResult(true, "原实例已经恢复，并复现原始 Prompt Template 失败签名。", candidate, transactionPath);
                }

                bool patchLoadWasLastStable = effectivePatchedInstanceId is null &&
                    (record.State == LmStudioTemplateTransactionState.OriginalUnloaded ||
                     record.LastStableState == LmStudioTemplateTransactionState.OriginalUnloaded) &&
                    record.FailureStage is not (LmStudioLifecycleStage.LoadOriginal or LmStudioLifecycleStage.ValidateOriginal or LmStudioLifecycleStage.ProbeOriginal);
                if (!patchLoadWasLastStable)
                {
                    throw new InvalidOperationException("同一源模型存在无法唯一归因的 loaded instance；为避免卸载错误实例，自动回滚已停止。");
                }

                failureStage = LmStudioLifecycleStage.UnloadPatched;
                string suspectedPatchedId = sameSource[0].Id;
                ReportProgress($"卸载未返回 ID 的疑似补丁实例 {suspectedPatchedId}");
                await client.UnloadAsync(suspectedPatchedId, cancellationToken).ConfigureAwait(false);
                models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
                if (models.Any(model => model.IsLoaded == true && model.Id.Equals(suspectedPatchedId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("未返回 instance_id 的疑似补丁实例卸载后仍存在。");
                }
            }

            failureStage = LmStudioLifecycleStage.LoadOriginal;
            await ValidateLoadTargetCurrentAsync(original, cancellationToken).ConfigureAwait(false);
            ReportProgress(originalPromptTemplate is null
                ? "按原精确变体与完整配置恢复内置模板实例"
                : $"按原精确变体与完整配置恢复运行时规则 {record.OriginalRuntimeRuleVersion}");
            LmStudioLoadResponse load;
            try
            {
                load = await client.LoadAsync(
                    original.SourceModelKey,
                    original.LoadConfiguration,
                    originalPromptTemplate,
                    PositiveTtl(original.RemainingTtlSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                LmStudioLoadedInstanceSnapshot? reconciled = await TryReconcileRestoredInstanceAsync(original, record, cancellationToken).ConfigureAwait(false);
                if (reconciled is not null)
                {
                    await UpdateRecordAsync(record, LmStudioTemplateTransactionState.RolledBack, null, $"恢复 load 响应异常，但 native 状态已证明原实例恢复为 {reconciled.InstanceId}。", cancellationToken).ConfigureAwait(false);
                    return new LmStudioRollbackResult(true, "恢复响应异常，但 native 配置与原始层级失败签名已证明原实例恢复。", reconciled, transactionPath);
                }

                throw;
            }

            if (!LmStudioClient.LoadConfigurationsEqual(original.LoadConfiguration, load.EchoedConfiguration))
            {
                throw new InvalidDataException("回滚 load_config 回显与原实例配置不一致。");
            }

            failureStage = LmStudioLifecycleStage.ValidateOriginal;
            LmStudioLoadedInstanceSnapshot restored = await CaptureAsync(load.InstanceId, cancellationToken).ConfigureAwait(false);
            ValidateReloadedSnapshot(original, restored);
            failureStage = LmStudioLifecycleStage.ProbeOriginal;
            if (!await ReproducesOriginalRuntimeAsync(restored.InstanceId, record, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("恢复实例没有复现事务记录中的原始运行时模板四阶段签名；拒绝将其标记为 RolledBack。");
            }

            await UpdateRecordAsync(record, LmStudioTemplateTransactionState.RolledBack, null, $"原实例已恢复为 {restored.InstanceId}。", cancellationToken).ConfigureAwait(false);
            logger.Info($"LM Studio template transaction rolled back: id={transactionId:N}, restored={restored.InstanceId}");
            ReportProgress($"RolledBack：已恢复原始实例 {restored.InstanceId}");
            return new LmStudioRollbackResult(true, $"已恢复原始 LM Studio 实例 {restored.InstanceId}。", restored, transactionPath);
        }
        catch (Exception exception)
        {
            string detail = $"事务式回滚失败：{exception.GetType().Name}: {exception.Message}";
            try
            {
                await UpdateRecordAsync(
                    record,
                    LmStudioTemplateTransactionState.RollbackFailed,
                    effectivePatchedInstanceId,
                    detail,
                    CancellationToken.None,
                    failureStage,
                    exception is LmStudioApiException apiException ? apiException.Failure : null,
                    record.RecoveryAttemptCount).ConfigureAwait(false);
            }
            catch (Exception journalException) when (journalException is IOException or UnauthorizedAccessException or JsonException)
            {
                detail += $"；事务记录更新也失败：{journalException.GetType().Name}";
            }

            logger.LogError($"LM Studio template rollback failed: id={transactionId:N}", exception);
            ReportProgress("RollbackFailed：自动恢复已停止");
            return new LmStudioRollbackResult(false, detail, null, transactionPath);
        }
    }

    private async Task<GgufChatTemplateAnalysis> ValidateJournalGgufAsync(
        LmStudioTemplateTransactionRecord record,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(record.GgufFilePath);
        if (!file.Exists ||
            !file.Name.Equals(record.GgufFileName, StringComparison.OrdinalIgnoreCase) ||
            file.Length != record.GgufLength ||
            file.LastWriteTimeUtc != record.GgufLastWriteTimeUtc.UtcDateTime)
        {
            throw new IOException("事务记录对应的 GGUF 文件已移动、缺失或发生变化；为避免恢复错误模型，自动生命周期操作已停止。");
        }

        GgufChatTemplateAnalysis analysis = await ggufReader.ReadAsync(record.GgufFilePath, cancellationToken).ConfigureAwait(false);
        if (analysis.GgufVersion != record.GgufVersion ||
            !analysis.TemplateSha256.Equals(record.OriginalTemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("事务记录对应的 GGUF 版本或 Prompt Template 哈希已变化；自动生命周期操作已停止。");
        }

        return analysis;
    }

    private async Task<LmStudioLoadedInstanceSnapshot> ValidatePlanBeforeMutationAsync(
        LmStudioTemplateRepairPlan plan,
        CancellationToken cancellationToken)
    {
        LmStudioLoadedInstanceSnapshot current = await CaptureAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!current.Fingerprint.Equals(plan.OriginalInstance.Fingerprint, StringComparison.Ordinal))
        {
            throw new IOException("LM Studio instance 在预览后发生变化；未卸载模型，请重新 Preview。");
        }

        if (plan.OriginalInstance.RemainingTtlSeconds is null && current.RemainingTtlSeconds is not null ||
            plan.OriginalInstance.RemainingTtlSeconds is > 0 && current.RemainingTtlSeconds is not (> 0) ||
            plan.OriginalInstance.RemainingTtlSeconds is > 0 && current.RemainingTtlSeconds > plan.OriginalInstance.RemainingTtlSeconds)
        {
            throw new IOException("LM Studio instance TTL 在预览后出现不安全变化；未卸载模型，请重新 Preview。");
        }

        await ValidateLoadTargetCurrentAsync(current, cancellationToken).ConfigureAwait(false);
        await client.ProbePromptTemplateSchemaAsync(cancellationToken).ConfigureAwait(false);

        var hierarchyProbe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
        CodexInstructionHierarchyProbeResult currentHierarchy = await hierarchyProbe.ProbeAsync(current.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!SameProbeSignature(currentHierarchy, plan.OriginalHierarchyProbe))
        {
            throw new IOException("LM Studio 四阶段指令层级行为在预览后发生变化；未卸载模型，请重新 Preview。");
        }

        FileInfo file = new(plan.GgufAnalysis.FilePath);
        if (!file.Exists || file.Length != plan.GgufAnalysis.FileLength || file.LastWriteTimeUtc != plan.GgufAnalysis.LastWriteTimeUtc.UtcDateTime)
        {
            throw new IOException("GGUF 文件在预览后发生变化；未卸载模型，请重新分析。");
        }

        GgufChatTemplateAnalysis analysis = await ggufReader.ReadAsync(plan.GgufAnalysis.FilePath, cancellationToken).ConfigureAwait(false);
        PromptTemplateRepairPreview preview = templateRepair.CreatePreview(analysis);
        if (!analysis.TemplateSha256.Equals(plan.GgufAnalysis.TemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            preview.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired) ||
            !string.Equals(preview.PatchedTemplateSha256, plan.TemplatePreview.PatchedTemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(preview.RuleVersion, plan.TemplatePreview.RuleVersion, StringComparison.Ordinal))
        {
            throw new IOException("GGUF Prompt Template 或修补结果在预览后发生变化；未卸载模型，请重新 Preview。");
        }

        await ValidateRuntimeProvenanceAsync(plan, current, analysis, cancellationToken).ConfigureAwait(false);

        return current;
    }

    private async Task ValidateRuntimeProvenanceAsync(
        LmStudioTemplateRepairPlan plan,
        LmStudioLoadedInstanceSnapshot current,
        GgufChatTemplateAnalysis analysis,
        CancellationToken cancellationToken)
    {
        LmStudioRuntimeTemplateProvenance provenance = plan.OriginalRuntimeTemplate;
        if (provenance.Mode == LmStudioRuntimeTemplateMode.BuiltIn)
        {
            if (provenance.RuleVersion is not null || provenance.TemplateSha256 is not null || provenance.EvidenceTransactionId is not null)
            {
                throw new InvalidDataException("内置模板 provenance 包含不应存在的管理器规则证据。");
            }

            return;
        }

        if (provenance.Mode != LmStudioRuntimeTemplateMode.ManagerRule ||
            provenance.RuleVersion != PromptTemplateRepairService.LegacyLeadingRuleVersion ||
            string.IsNullOrWhiteSpace(provenance.TemplateSha256) ||
            provenance.EvidenceTransactionId is not Guid evidenceId)
        {
            throw new InvalidDataException("当前运行时模板 provenance 不完整或不是可恢复的 v2 规则。");
        }

        LmStudioTemplateTransactionRecord evidence = await transactions.ReadAsync(evidenceId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("v2 completed provenance 事务记录已不存在。", transactions.GetPath(evidenceId));
        if (evidence.State != LmStudioTemplateTransactionState.Completed ||
            evidence.RuleVersion != PromptTemplateRepairService.LegacyLeadingRuleVersion ||
            !string.Equals(evidence.PatchedInstanceId, current.InstanceId, StringComparison.Ordinal) ||
            !evidence.PatchedTemplateSha256.Equals(provenance.TemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            !evidence.OriginalInstance.Endpoint.AbsoluteUri.TrimEnd('/').Equals(current.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            !evidence.OriginalInstance.SourceModelKey.Equals(current.SourceModelKey, StringComparison.OrdinalIgnoreCase) ||
            !SameOptional(evidence.OriginalInstance.SelectedVariant, current.SelectedVariant) ||
            !SameOptional(evidence.OriginalInstance.Architecture, current.Architecture) ||
            !SameOptional(evidence.OriginalInstance.Quantization, current.Quantization) ||
            !SameOptional(evidence.OriginalInstance.Parameters, current.Parameters) ||
            evidence.OriginalInstance.MaxContextLength != current.MaxContextLength ||
            evidence.GgufLength != analysis.FileLength ||
            evidence.GgufLastWriteTimeUtc != analysis.LastWriteTimeUtc ||
            evidence.GgufVersion != analysis.GgufVersion ||
            !evidence.OriginalTemplateSha256.Equals(analysis.TemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            !LmStudioClient.LoadConfigurationsEqual(evidence.OriginalInstance.LoadConfiguration, current.LoadConfiguration))
        {
            throw new IOException("v2 completed provenance 在预览后发生变化或不再匹配当前实例；未卸载模型。");
        }

        string recreated = templateRepair.RecreateKnownTemplate(analysis, provenance.RuleVersion, provenance.TemplateSha256);
        if (!string.Equals(recreated, plan.OriginalRuntimeTemplateText, StringComparison.Ordinal))
        {
            throw new IOException("确定性重建的 v2 模板与预览内容不一致；未卸载模型。");
        }
    }

    private static bool SameProbeSignature(
        CodexInstructionHierarchyProbeResult left,
        CodexInstructionHierarchyProbeResult right) =>
        left.Control == right.Control &&
        left.LeadingDeveloper == right.LeadingDeveloper &&
        left.ConversationControl == right.ConversationControl &&
        left.ContinuationDeveloper == right.ContinuationDeveloper &&
        string.Equals(left.FailureCode, right.FailureCode, StringComparison.Ordinal);

    private async Task EnsureCodexClosedAsync(CancellationToken cancellationToken)
    {
        CodexEnvironmentInfo environment = await runtimeProbe.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (environment.IsRunning)
        {
            throw new InvalidOperationException("应用兼容模板前必须完全关闭 Codex Desktop、CLI 与 helper 进程。");
        }
    }

    private async Task<LmStudioLoadedInstanceSnapshot?> TryCaptureAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile? model = models.SingleOrDefault(model => model.IsLoaded == true && model.Id.Equals(instanceId, StringComparison.Ordinal));
        return model is null ? null : CreateSnapshot(model);
    }

    private async Task EnsureSourceAbsentAsync(string sourceModelKey, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models.Any(model =>
            model.IsLoaded == true &&
            string.Equals(model.SourceModelKey, sourceModelKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("卸载确认期间出现同一源模型的其他 loaded instance；未发送补丁 load 请求，必须刷新后重试。");
        }
    }

    private async Task<LmStudioLoadTarget> ValidateLoadTargetCurrentAsync(
        LmStudioLoadedInstanceSnapshot original,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile? source = models.FirstOrDefault(model =>
            string.Equals(model.SourceModelKey, original.SourceModelKey, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            throw new InvalidOperationException($"LM Studio native API 不再报告源模型 {original.SourceModelKey}；自动重载已阻断。");
        }

        LmStudioLoadTarget current = CreateLoadTarget(source);
        LmStudioLoadTarget? expected = original.LoadTarget;
        bool variantsMatch = expected is null || expected.AvailableVariants.Count == 0 ||
            expected.AvailableVariants.SequenceEqual(current.AvailableVariants, StringComparer.OrdinalIgnoreCase);
        bool formatMatches = expected is null || string.IsNullOrWhiteSpace(expected.Format) || SameOptional(expected.Format, current.Format);
        if (!string.Equals(original.SourceModelKey, current.ModelKey, StringComparison.OrdinalIgnoreCase) ||
            !SameOptional(original.SelectedVariant, current.SelectedVariant) ||
            !SameOptional(original.Architecture, current.Architecture) ||
            !SameOptional(original.Quantization, current.Quantization) ||
            !SameOptional(original.Parameters, current.Parameters) ||
            original.MaxContextLength != current.MaxContextLength ||
            !variantsMatch ||
            !formatMatches)
        {
            throw new IOException("LM Studio 源模型的 selected variant、量化、架构或可用变体在预览后发生变化；自动重载已阻断。");
        }

        return current;
    }

    private LmStudioLoadedInstanceSnapshot CreateSnapshot(ModelProfile model)
    {
        if (model.IsLoaded != true || string.IsNullOrWhiteSpace(model.SourceModelKey) || model.LoadedConfiguration?.ContextLength is not > 0)
        {
            throw new InvalidDataException("LM Studio native API 未提供可事务重载的完整 loaded instance/context 数据。");
        }

        LmStudioLoadTarget loadTarget = CreateLoadTarget(model);

        var withoutFingerprint = new LmStudioLoadedInstanceSnapshot(
            endpoint,
            model.SourceModelKey,
            model.Id,
            model.SelectedVariant,
            model.Architecture,
            model.Quantization,
            model.Parameters,
            model.ModelType,
            model.MaxContextLength,
            model.LoadedConfiguration,
            model.RemainingTtlSeconds,
            requiresAuthentication,
            DateTimeOffset.Now,
            string.Empty,
            loadTarget);
        return withoutFingerprint with { Fingerprint = ComputeFingerprint(withoutFingerprint) };
    }

    private static LmStudioLoadTarget CreateLoadTarget(ModelProfile model)
    {
        string modelKey = model.SourceModelKey ?? throw new InvalidDataException("LM Studio native API 未提供源模型 key。");
        string[] variants = (model.AvailableVariants ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (variants.Length > 1 && string.IsNullOrWhiteSpace(model.SelectedVariant))
        {
            throw new InvalidDataException("LM Studio 模型包含多个变体但未报告 selected_variant；自动重载已阻断。");
        }

        if (!string.IsNullOrWhiteSpace(model.SelectedVariant) &&
            variants.Length > 0 &&
            !variants.Contains(model.SelectedVariant, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("LM Studio selected_variant 不属于 native API 返回的 variants；自动重载已阻断。");
        }

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ModelKey = modelKey,
            model.SelectedVariant,
            AvailableVariants = variants,
            model.Architecture,
            model.Quantization,
            model.Parameters,
            model.Format,
            model.MaxContextLength,
        });
        return new LmStudioLoadTarget(
            modelKey,
            model.SelectedVariant,
            variants,
            model.Architecture,
            model.Quantization,
            model.Parameters,
            model.Format,
            model.MaxContextLength,
            Convert.ToHexString(SHA256.HashData(canonical)));
    }

    private static string ComputeFingerprint(LmStudioLoadedInstanceSnapshot snapshot)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            endpoint = snapshot.Endpoint.AbsoluteUri.TrimEnd('/'),
            snapshot.SourceModelKey,
            snapshot.InstanceId,
            snapshot.SelectedVariant,
            snapshot.Architecture,
            snapshot.Quantization,
            snapshot.Parameters,
            snapshot.ModelType,
            snapshot.MaxContextLength,
            snapshot.LoadConfiguration,
            snapshot.RequiresAuthentication,
            snapshot.LoadTarget,
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string ComputeRecoveryAssessmentFingerprint(
        LmStudioTemplateTransactionRecord transaction,
        IReadOnlyList<LmStudioRecoveryCandidate> candidates,
        LmStudioRecoveryDisposition disposition,
        string? instanceToUnload)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            transaction.TransactionId,
            transaction.SchemaVersion,
            transaction.State,
            transaction.UpdatedAt,
            transaction.PatchedInstanceId,
            transaction.CandidateInstanceId,
            transaction.LastStableState,
            transaction.FailureStage,
            transaction.GgufLength,
            transaction.GgufLastWriteTimeUtc,
            transaction.OriginalTemplateSha256,
            transaction.OriginalRuntimeTemplateMode,
            transaction.OriginalRuntimeRuleVersion,
            transaction.OriginalRuntimeTemplateSha256,
            transaction.OriginalRuntimeEvidenceTransactionId,
            transaction.TargetRuntimeRuleVersion,
            Candidates = candidates.Select(candidate => new
            {
                candidate.Snapshot.InstanceId,
                candidate.Snapshot.Fingerprint,
                candidate.MatchesOriginalSnapshot,
                candidate.ReproducesOriginalFailure,
                candidate.HierarchyProbe?.Control,
                candidate.HierarchyProbe?.LeadingDeveloper,
                candidate.HierarchyProbe?.ConversationControl,
                candidate.HierarchyProbe?.ContinuationDeveloper,
                candidate.HierarchyProbe?.FailureCode,
            }).ToArray(),
            Disposition = disposition,
            InstanceToUnload = instanceToUnload,
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static void ValidateReloadedSnapshot(
        LmStudioLoadedInstanceSnapshot original,
        LmStudioLoadedInstanceSnapshot reloaded)
    {
        if (!string.Equals(original.SourceModelKey, reloaded.SourceModelKey, StringComparison.OrdinalIgnoreCase) ||
            !SameOptional(original.SelectedVariant, reloaded.SelectedVariant) ||
            !SameOptional(original.Architecture, reloaded.Architecture) ||
            !SameOptional(original.Quantization, reloaded.Quantization) ||
            !SameOptional(original.ModelType, reloaded.ModelType) ||
            original.MaxContextLength != reloaded.MaxContextLength ||
            original.LoadTarget is not null && reloaded.LoadTarget is not null &&
            !original.LoadTarget.Fingerprint.Equals(reloaded.LoadTarget.Fingerprint, StringComparison.Ordinal) ||
            !LmStudioClient.LoadConfigurationsEqual(original.LoadConfiguration, reloaded.LoadConfiguration))
        {
            throw new InvalidDataException("重载后的模型变体、架构、量化、上下文或加载配置与原实例不一致。");
        }

        if (original.RemainingTtlSeconds is null && reloaded.RemainingTtlSeconds is not null ||
            original.RemainingTtlSeconds is > 0 && reloaded.RemainingTtlSeconds is not (> 0) ||
            original.RemainingTtlSeconds is > 0 && reloaded.RemainingTtlSeconds > original.RemainingTtlSeconds)
        {
            throw new InvalidDataException("重载后的 TTL 与原实例剩余 TTL 不一致。");
        }

        if (original.RemainingTtlSeconds is > 0 && reloaded.RemainingTtlSeconds is > 0)
        {
            int elapsedSeconds = (int)Math.Ceiling(Math.Max(0, (DateTimeOffset.Now - original.CapturedAt).TotalSeconds));
            int minimumReasonableTtl = Math.Max(1, original.RemainingTtlSeconds.Value - elapsedSeconds - 30);
            if (reloaded.RemainingTtlSeconds < minimumReasonableTtl)
            {
                throw new InvalidDataException("重载后的剩余 TTL 下降幅度超过本次操作耗时与安全容差。");
            }
        }
    }

    private static bool SnapshotsReloadEquivalent(
        LmStudioLoadedInstanceSnapshot original,
        LmStudioLoadedInstanceSnapshot candidate)
    {
        try
        {
            ValidateReloadedSnapshot(original, candidate);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private LmStudioPromptTemplateConfiguration? RecreateOriginalRuntimeTemplate(
        LmStudioTemplateTransactionRecord record,
        GgufChatTemplateAnalysis analysis)
    {
        if (record.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.BuiltIn)
        {
            return null;
        }

        if (record.OriginalRuntimeTemplateMode != LmStudioRuntimeTemplateMode.ManagerRule ||
            string.IsNullOrWhiteSpace(record.OriginalRuntimeRuleVersion) ||
            string.IsNullOrWhiteSpace(record.OriginalRuntimeTemplateSha256) ||
            record.OriginalRuntimeEvidenceTransactionId is null)
        {
            throw new InvalidDataException("事务缺少可确定性恢复的原运行时模板 provenance；未执行生命周期操作。");
        }

        string template = templateRepair.RecreateKnownTemplate(
            analysis,
            record.OriginalRuntimeRuleVersion,
            record.OriginalRuntimeTemplateSha256);
        return new LmStudioPromptTemplateConfiguration("jinja", template, []);
    }

    private async Task ValidateOriginalRuntimeEvidenceAsync(
        LmStudioTemplateTransactionRecord record,
        GgufChatTemplateAnalysis analysis,
        CancellationToken cancellationToken)
    {
        if (record.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.BuiltIn)
        {
            return;
        }

        if (record.OriginalRuntimeEvidenceTransactionId is not Guid evidenceId ||
            string.IsNullOrWhiteSpace(record.OriginalRuntimeRuleVersion) ||
            string.IsNullOrWhiteSpace(record.OriginalRuntimeTemplateSha256))
        {
            throw new InvalidDataException("事务缺少原运行时管理器模板的 completed provenance。");
        }

        LmStudioTemplateTransactionRecord evidence = await transactions.ReadAsync(evidenceId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("原运行时模板的 completed provenance journal 已不存在。", transactions.GetPath(evidenceId));
        if (evidence.State != LmStudioTemplateTransactionState.Completed ||
            !evidence.RuleVersion.Equals(record.OriginalRuntimeRuleVersion, StringComparison.Ordinal) ||
            !evidence.PatchedTemplateSha256.Equals(record.OriginalRuntimeTemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.PatchedInstanceId, record.OriginalInstance.InstanceId, StringComparison.Ordinal) ||
            !evidence.OriginalInstance.Endpoint.AbsoluteUri.TrimEnd('/').Equals(record.OriginalInstance.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            !evidence.OriginalInstance.SourceModelKey.Equals(record.OriginalInstance.SourceModelKey, StringComparison.OrdinalIgnoreCase) ||
            !SameOptional(evidence.OriginalInstance.SelectedVariant, record.OriginalInstance.SelectedVariant) ||
            !SameOptional(evidence.OriginalInstance.Architecture, record.OriginalInstance.Architecture) ||
            !SameOptional(evidence.OriginalInstance.Quantization, record.OriginalInstance.Quantization) ||
            !SameOptional(evidence.OriginalInstance.Parameters, record.OriginalInstance.Parameters) ||
            evidence.OriginalInstance.MaxContextLength != record.OriginalInstance.MaxContextLength ||
            evidence.GgufLength != analysis.FileLength ||
            evidence.GgufLastWriteTimeUtc != analysis.LastWriteTimeUtc ||
            evidence.GgufVersion != analysis.GgufVersion ||
            !evidence.OriginalTemplateSha256.Equals(analysis.TemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            !LmStudioClient.LoadConfigurationsEqual(evidence.OriginalInstance.LoadConfiguration, record.OriginalInstance.LoadConfiguration))
        {
            throw new InvalidDataException("原运行时模板的 completed provenance 与 v3 事务快照不一致；未执行自动恢复。");
        }
    }

    private async Task<bool> ReproducesOriginalRuntimeAsync(
        string instanceId,
        LmStudioTemplateTransactionRecord record,
        CancellationToken cancellationToken)
    {
        var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ReproducesOriginalRuntimeSignature(result, record);
    }

    private static bool ReproducesOriginalRuntimeSignature(
        CodexInstructionHierarchyProbeResult result,
        LmStudioTemplateTransactionRecord record)
    {
        if (record.SchemaVersion >= 3)
        {
            return record.OriginalHierarchyProbe is not null &&
                SameProbeSignature(result, record.OriginalHierarchyProbe);
        }

        if (record.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.ManagerRule)
        {
            return record.OriginalRuntimeRuleVersion == PromptTemplateRepairService.LegacyLeadingRuleVersion &&
                result.Control.Passed &&
                result.LeadingDeveloper.Passed &&
                result.ConversationControl.Passed &&
                !result.ContinuationDeveloper.Passed &&
                string.Equals(result.FailureCode, CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder, StringComparison.Ordinal);
        }

        return result.Control.Passed &&
            !result.HierarchyPassed &&
            string.Equals(result.FailureCode, record.FailureCode, StringComparison.Ordinal);
    }

    private async Task<LmStudioLoadedInstanceSnapshot?> TryReconcileRestoredInstanceAsync(
        LmStudioLoadedInstanceSnapshot original,
        LmStudioTemplateTransactionRecord record,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile[] sameSource = models.Where(model =>
            model.IsLoaded == true &&
            string.Equals(model.SourceModelKey, original.SourceModelKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sameSource.Length != 1)
        {
            return null;
        }

        LmStudioLoadedInstanceSnapshot candidate = CreateSnapshot(sameSource[0]);
        return SnapshotsReloadEquivalent(original, candidate) &&
            await ReproducesOriginalRuntimeAsync(candidate.InstanceId, record, cancellationToken).ConfigureAwait(false)
                ? candidate
                : null;
    }

    private static bool SameOptional(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) ||
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static int? PositiveTtl(int? ttl) => ttl is > 0 ? ttl : null;

    private void EnsureControllerMatches(LmStudioLoadedInstanceSnapshot snapshot)
    {
        if (!endpoint.AbsoluteUri.TrimEnd('/').Equals(snapshot.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LM Studio endpoint 与事务预览不一致。");
        }
    }

    private static LmStudioTemplateTransactionRecord CreateRecord(LmStudioTemplateRepairPlan plan) => new(
        3,
        plan.TransactionId,
        LmStudioTemplateTransactionState.Prepared,
        plan.CreatedAt,
        DateTimeOffset.Now,
        plan.OriginalInstance,
        plan.FailureCode,
        Path.GetFullPath(plan.GgufAnalysis.FilePath),
        plan.GgufAnalysis.FileName,
        plan.GgufAnalysis.FileLength,
        plan.GgufAnalysis.LastWriteTimeUtc,
        plan.GgufAnalysis.GgufVersion,
        plan.GgufAnalysis.TemplateSha256,
        plan.TemplatePreview.PatchedTemplateSha256!,
        plan.TemplatePreview.RuleVersion,
        null,
        "等待卸载原实例。",
        LoadModelKey: plan.OriginalInstance.SourceModelKey,
        LastStableState: LmStudioTemplateTransactionState.Prepared,
        OriginalRuntimeTemplateMode: plan.OriginalRuntimeTemplate.Mode,
        OriginalRuntimeRuleVersion: plan.OriginalRuntimeTemplate.RuleVersion,
        OriginalRuntimeTemplateSha256: plan.OriginalRuntimeTemplate.TemplateSha256,
        OriginalRuntimeEvidenceTransactionId: plan.OriginalRuntimeTemplate.EvidenceTransactionId,
        TargetRuntimeRuleVersion: plan.TemplatePreview.RuleVersion,
        OriginalHierarchyProbe: plan.OriginalHierarchyProbe);

    private async Task<LmStudioTemplateTransactionRecord> RecordApplyFailureEvidenceAsync(
        LmStudioTemplateTransactionRecord record,
        string sourceModelKey,
        string? patchedInstanceId,
        LmStudioLifecycleStage failureStage,
        Exception exception)
    {
        try
        {
            IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(CancellationToken.None).ConfigureAwait(false);
            string[] after = models
                .Where(model => model.IsLoaded == true && string.Equals(model.SourceModelKey, sourceModelKey, StringComparison.OrdinalIgnoreCase))
                .Select(model => model.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string? candidate = string.IsNullOrWhiteSpace(patchedInstanceId) && after.Length == 1
                ? after[0]
                : record.CandidateInstanceId;
            LmStudioTemplateTransactionRecord updated = record with
            {
                SchemaVersion = Math.Max(record.SchemaVersion, 2),
                UpdatedAt = DateTimeOffset.Now,
                LoadModelKey = record.LoadModelKey ?? sourceModelKey,
                LastStableState = record.LastStableState ?? record.State,
                FailureStage = failureStage,
                LastApiFailure = exception is LmStudioApiException apiException ? apiException.Failure : record.LastApiFailure,
                SameSourceInstanceIdsBeforeLoad = record.SameSourceInstanceIdsBeforeLoad ?? [],
                SameSourceInstanceIdsAfterLoad = after,
                CandidateInstanceId = candidate,
            };
            await transactions.WriteAsync(updated, CancellationToken.None).ConfigureAwait(false);
            return updated;
        }
        catch (Exception evidenceException) when (evidenceException is not OperationCanceledException)
        {
            logger.Warning($"LM Studio apply failure evidence unavailable: {evidenceException.GetType().Name}");
            return record;
        }
    }

    private async Task<LmStudioTemplateTransactionRecord> UpdateRecordAsync(
        LmStudioTemplateTransactionRecord record,
        LmStudioTemplateTransactionState state,
        string? patchedInstanceId,
        string detail,
        CancellationToken cancellationToken,
        LmStudioLifecycleStage failureStage = LmStudioLifecycleStage.None,
        LmStudioApiFailure? apiFailure = null,
        int? recoveryAttemptCount = null)
    {
        LmStudioTemplateTransactionRecord updated = record with
        {
            SchemaVersion = Math.Max(record.SchemaVersion, 2),
            State = state,
            UpdatedAt = DateTimeOffset.Now,
            PatchedInstanceId = patchedInstanceId,
            Detail = detail,
            LoadModelKey = record.LoadModelKey ?? record.OriginalInstance.SourceModelKey,
            LastStableState = state == LmStudioTemplateTransactionState.RollbackFailed
                ? record.LastStableState ?? (record.State == LmStudioTemplateTransactionState.RollbackFailed ? null : record.State)
                : state,
            SameSourceInstanceIdsBeforeLoad = state == LmStudioTemplateTransactionState.OriginalUnloaded
                ? []
                : record.SameSourceInstanceIdsBeforeLoad,
            SameSourceInstanceIdsAfterLoad = (state is LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified) &&
                !string.IsNullOrWhiteSpace(patchedInstanceId)
                    ? [patchedInstanceId]
                    : record.SameSourceInstanceIdsAfterLoad,
            CandidateInstanceId = state is LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified
                ? patchedInstanceId ?? record.CandidateInstanceId
                : record.CandidateInstanceId,
            FailureStage = state == LmStudioTemplateTransactionState.RollbackFailed
                ? record.FailureStage
                : failureStage == LmStudioLifecycleStage.None ? record.FailureStage : failureStage,
            LastApiFailure = apiFailure ?? record.LastApiFailure,
            RecoveryAttemptCount = recoveryAttemptCount ?? record.RecoveryAttemptCount,
            LastRecoveryFailureStage = state == LmStudioTemplateTransactionState.RollbackFailed && failureStage != LmStudioLifecycleStage.None
                ? failureStage
                : record.LastRecoveryFailureStage,
        };
        await transactions.WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private void AcquireLifecycleLease(Guid transactionId)
    {
        if (lifecycleLease is not null)
        {
            if (lifecycleLeaseTransactionId == transactionId)
            {
                return;
            }

            throw new InvalidOperationException($"当前控制器仍持有另一个 LM Studio 生命周期事务 {lifecycleLeaseTransactionId:N}。");
        }

        string path = transactions.LifecycleLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            lifecycleLease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            lifecycleLeaseTransactionId = transactionId;
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("另一个 Codex Model Manager 进程正在执行 LM Studio 模型生命周期事务；本次操作未修改实例。", exception);
        }
    }

    private void ReleaseLifecycleLease(Guid transactionId)
    {
        if (lifecycleLease is null)
        {
            return;
        }

        if (lifecycleLeaseTransactionId != transactionId)
        {
            throw new InvalidOperationException("LM Studio 生命周期锁所属事务与释放请求不一致。");
        }

        lifecycleLease.Dispose();
        lifecycleLease = null;
        lifecycleLeaseTransactionId = null;
    }

    private void ReportProgress(string stage)
    {
        logger.Info("LM Studio lifecycle stage: " + stage);
        try
        {
            ProgressChanged?.Invoke(this, stage);
        }
        catch (InvalidOperationException exception)
        {
            // UI progress is observational only. A closing/disposed WinForms
            // subscriber must never turn a verified lifecycle step into a rollback.
            logger.Warning($"LM Studio lifecycle progress subscriber unavailable: {exception.GetType().Name}");
        }
    }

    private static string ShortHash(string value) => value[..Math.Min(12, value.Length)];
}

public sealed class LmStudioTemplateApplyException : InvalidOperationException
{
    public LmStudioTemplateApplyException(
        string message,
        Exception innerException,
        LmStudioRollbackResult rollback,
        LmStudioTemplateRepairPlan plan,
        LmStudioLifecycleStage failureStage)
        : base(message, innerException)
    {
        Rollback = rollback;
        Plan = plan;
        FailureStage = failureStage;
    }

    public LmStudioRollbackResult Rollback { get; }

    public LmStudioTemplateRepairPlan Plan { get; }

    public LmStudioLifecycleStage FailureStage { get; }
}
