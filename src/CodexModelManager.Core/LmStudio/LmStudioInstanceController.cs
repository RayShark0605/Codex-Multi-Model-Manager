using System.Security.Cryptography;
using System.Text.Json;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Codex;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;
using CodexModelManager.Core.Providers;

namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// LM Studio 实例生命周期控制器：以事务 journal（schema v3/v4）驱动
/// “备份 defaults → 卸载原实例 → 加载补丁实例 → 四阶段验证 → 提交/回滚”的完整流程，
/// 失败自动回滚并可恢复中断的事务。文件级生命周期锁保证同一时刻只有一个进程在变更实例。
/// </summary>
public sealed class LmStudioInstanceController : ILmStudioInstanceController
{
    internal static readonly TimeSpan AutomaticRollbackTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan FailureEvidenceTimeout = TimeSpan.FromSeconds(5);
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
    private readonly LmStudioPerModelDefaultsStore? perModelDefaultsStore;
    private readonly Func<string?> lmStudioVersionProvider;
    private readonly ILmStudioModelFileLocator? modelFileLocator;
    private FileStream? lifecycleLease;
    private Guid? lifecycleLeaseTransactionId;

    /// <summary>注入全部协作者构造控制器；端点须通过安全策略校验。</summary>
    public LmStudioInstanceController(
        Uri endpoint,
        bool requiresAuthentication,
        HttpClient httpClient,
        Func<string?>? tokenProvider,
        ICodexRuntimeProbe runtimeProbe,
        IGgufChatTemplateReader ggufReader,
        IPromptTemplateRepairService templateRepair,
        LmStudioTemplateTransactionStore transactions,
        IAppLogger logger,
        LmStudioPerModelDefaultsStore? perModelDefaultsStore = null,
        Func<string?>? lmStudioVersionProvider = null,
        ILmStudioModelFileLocator? modelFileLocator = null)
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
        this.perModelDefaultsStore = perModelDefaultsStore;
        this.lmStudioVersionProvider = lmStudioVersionProvider ?? LmStudioLocalVersionDetector.Detect;
        this.modelFileLocator = modelFileLocator;
        client = new LmStudioClient(endpoint, this.tokenProvider, httpClient);
    }

    /// <summary>生命周期阶段推进事件（观察性，UI 展示用）。</summary>
    public event EventHandler<string>? ProgressChanged;

    /// <summary>底层 HTTP 客户端的请求超时（测试用）。</summary>
    internal TimeSpan RequestTimeout => httpClient.Timeout;

    /// <summary>释放生命周期锁。</summary>
    public void Dispose()
    {
        lifecycleLease?.Dispose();
        lifecycleLease = null;
        lifecycleLeaseTransactionId = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>捕获指定实例的权威快照；要求实例唯一且同源唯一，否则拒绝生命周期操作。</summary>
    public async Task<LmStudioLoadedInstanceSnapshot> CaptureAsync(string instanceId, CancellationToken cancellationToken = default)
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
        if (sourceModelKey is not null)
        {
            EnsureNativeSourceIdentityUsable(sourceModelKey);
        }

        int sameSourceCount = models.Count(model => model.IsLoaded == true && !string.IsNullOrWhiteSpace(sourceModelKey) && string.Equals(model.SourceModelKey, sourceModelKey, StringComparison.OrdinalIgnoreCase));
        if (sameSourceCount != 1)
        {
            throw new InvalidOperationException("同一源模型存在多个 loaded instance；无法唯一证明待修复实例，自动生命周期操作已阻断。");
        }

        return CreateSnapshot(matches[0]);
    }

    /// <summary>
    /// 执行模板修复事务：前置校验 → 写入 Prepared journal →（可选）备份并持久化 defaults →
    /// 卸载原实例 → 加载补丁实例 → 验证快照与四阶段指令层级 → PatchedAndVerified 并保留生命周期锁等待最终确认。
    /// 任何一步失败都会记录失败证据并自动回滚（30 分钟超时），最终以 <see cref="LmStudioTemplateApplyException"/> 抛出。
    /// </summary>
    public async Task<LmStudioTemplateRepairResult> ApplyTemplateAsync(LmStudioTemplateRepairPlan plan, CancellationToken cancellationToken = default)
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
            LogSafely(() => logger.Info($"LM Studio template transaction prepared: id={plan.TransactionId:N}, instance={plan.OriginalInstance.InstanceId}, variant={plan.OriginalInstance.SelectedVariant ?? plan.OriginalInstance.SourceModelKey}, originalSha={ShortHash(plan.GgufAnalysis.TemplateSha256)}, patchedSha={ShortHash(plan.TemplatePreview.PatchedTemplateSha256!)}"));

            string? patchedInstanceId = null;
            bool patchedLoadStarted = false;
            LmStudioLifecycleStage failureStage = plan.PersistentDefaults is null ? LmStudioLifecycleStage.UnloadOriginal : LmStudioLifecycleStage.PersistDefaults;
            try
            {
                if (plan.PersistentDefaults is not null)
                {
                    LmStudioPerModelDefaultsStore defaultsStore = perModelDefaultsStore ?? throw new InvalidOperationException("schema-v4 计划缺少 per-model defaults 存储服务。");
                    ReportProgress("使用 CurrentUser DPAPI 备份 LM Studio per-model defaults");
                    string backupPath = transactions.GetEncryptedDefaultsBackupPath(plan.TransactionId);
                    LmStudioDefaultsBackupArtifact backup = await defaultsStore.CreateVerifiedBackupAsync(plan.PersistentDefaults, backupPath, cancellationToken).ConfigureAwait(false);
                    record = await UpdatePersistenceRecordAsync(record, LmStudioPersistenceStage.BackupVerified, "per-model defaults 的 DPAPI 备份已写入并完成解密/SHA 校验。", cancellationToken, backup).ConfigureAwait(false);

                    ReportProgress(plan.PersistentDefaults.Mutation switch
                    {
                        LmStudioPerModelDefaultsMutation.Add => "持久化新增模型级 Prompt Template v3",
                        LmStudioPerModelDefaultsMutation.Upgrade => "事务式升级模型级 Prompt Template v2 → v3",
                        _ => "验证现有模型级 Prompt Template v3（No-op）",
                    });
                    await defaultsStore.ApplyAsync(plan.PersistentDefaults, cancellationToken).ConfigureAwait(false);
                    record = await UpdatePersistenceRecordAsync(record, LmStudioPersistenceStage.DefaultsVerified, "per-model defaults 候选已原子写入并重读验证。", cancellationToken).ConfigureAwait(false);

                    await ValidatePlanAfterDefaultsWriteAsync(plan, cancellationToken).ConfigureAwait(false);
                }

                ReportProgress("卸载原始 LM Studio 实例");
                failureStage = LmStudioLifecycleStage.UnloadOriginal;
                await UnloadOriginalWithReconciliationAsync(plan, cancellationToken).ConfigureAwait(false);
                if (await TryCaptureAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidOperationException("LM Studio unload 返回成功，但原 instance 仍存在；已停止加载补丁实例。");
                }

                await EnsureSourceAbsentAsync(plan.OriginalInstance.SourceModelKey, cancellationToken).ConfigureAwait(false);

                record = await UpdateRecordAsync(record, LmStudioTemplateTransactionState.OriginalUnloaded, null, "原实例已卸载。", cancellationToken).ConfigureAwait(false);
                ReportProgress(plan.PersistentDefaults is null
                    ? "加载带运行时 Prompt Template 的精确模型变体"
                    : "按持久 per-model defaults 重载精确模型变体（REST 请求不含 prompt_template）");
                failureStage = LmStudioLifecycleStage.LoadPatched;
                await ValidateLoadTargetCurrentAsync(plan.OriginalInstance, cancellationToken).ConfigureAwait(false);
                LmStudioPromptTemplateConfiguration? promptTemplate = plan.PersistentDefaults is null
                    ? new LmStudioPromptTemplateConfiguration("jinja", plan.TemplatePreview.PatchedTemplate!, [])
                    : null;
                cancellationToken.ThrowIfCancellationRequested();
                patchedLoadStarted = true;
                LmStudioLoadResponse load = await LoadPatchedWithReconciliationAsync(plan, promptTemplate, cancellationToken).ConfigureAwait(false);
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
                CodexInstructionHierarchyProbeResult hierarchy = await ProbeWithRetryAsync(patched, cancellationToken).ConfigureAwait(false);
                if (!hierarchy.IsCompatible)
                {
                    throw new LmStudioCompatibilityException(hierarchy);
                }

                // 层级请求是“运行时模板已生效”的权威证明。之后再重读一次 native 状态，
                // 防止并发的 unload/reload 被误报为已验证结果。
                patched = await CaptureAsync(patchedInstanceId, cancellationToken).ConfigureAwait(false);
                ValidateReloadedSnapshot(plan.OriginalInstance, patched);
                if (plan.PersistentDefaults is not null)
                {
                    await LmStudioPerModelDefaultsStore.VerifyAppliedAsync(plan.PersistentDefaults, cancellationToken).ConfigureAwait(false);
                    record = await UpdatePersistenceRecordAsync(record, LmStudioPersistenceStage.PersistentDefaultVerified, "无 REST prompt_template 的重载通过四阶段探针，持久 defaults 再次验证未漂移。", cancellationToken).ConfigureAwait(false);
                }

                record = await UpdateRecordAsync(record, LmStudioTemplateTransactionState.PatchedAndVerified, patchedInstanceId, "补丁实例 Codex 指令层级 PASS。", cancellationToken).ConfigureAwait(false);
                LogSafely(() => logger.Info($"LM Studio template transaction verified: id={plan.TransactionId:N}, instance={patchedInstanceId}, control={hierarchy.Control.HttpStatus}, leading={hierarchy.LeadingDeveloper.HttpStatus}, conversation={hierarchy.ConversationControl.HttpStatus}, continuation={hierarchy.ContinuationDeveloper.HttpStatus}"));
                ReportProgress("PatchedAndVerified：等待 Codex 配置最终确认");
                retainLease = true;
                return new LmStudioTemplateRepairResult(plan, patched, hierarchy, transactions.GetPath(plan.TransactionId));
            }
            catch (Exception exception)
            {
                record = await RecordApplyFailureEvidenceAsync(record, plan.OriginalInstance.SourceModelKey, patchedInstanceId, patchedLoadStarted, failureStage, exception).ConfigureAwait(false);
                patchedInstanceId ??= record.CandidateInstanceId;
                ReportProgress(plan.OriginalRuntimeTemplate.Mode == LmStudioRuntimeTemplateMode.ManagerRule
                    ? $"修复失败：正在事务式恢复原运行时规则 {plan.OriginalRuntimeTemplate.RuleVersion}"
                    : "修复失败：正在事务式恢复原始内置模板");
                using var rollbackTimeout = new CancellationTokenSource(AutomaticRollbackTimeout);
                LmStudioRollbackResult rollback = await RollbackCoreAsync(plan.OriginalInstance, plan.TransactionId, patchedInstanceId, record, rollbackTimeout.Token, patchedLoadStarted).ConfigureAwait(false);
                throw new LmStudioTemplateApplyException($"LM Studio 运行时 Prompt Template 修复失败。{rollback.Detail}", exception, rollback, plan, failureStage);
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

    /// <summary>卸载原实例；瞬态失败时在重试预算内先复核实例身份与四阶段签名未变，再重试一次。</summary>
    private async Task UnloadOriginalWithReconciliationAsync(LmStudioTemplateRepairPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            await client.UnloadAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SwitchRetryBudget.IsTransient(exception, cancellationToken))
        {
            if (!await SwitchRetryBudget.TryConsumeAsync(cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            LmStudioLoadedInstanceSnapshot? current = await TryCaptureAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await EnsureSourceAbsentAsync(plan.OriginalInstance.SourceModelKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!current.Fingerprint.Equals(plan.OriginalInstance.Fingerprint, StringComparison.Ordinal))
            {
                throw new IOException("unload 响应异常后原实例发生变化；不会重复卸载。", exception);
            }

            CodexInstructionHierarchyProbeResult hierarchy = await new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider)
                .ProbeAsync(current.InstanceId, cancellationToken).ConfigureAwait(false);
            if (!SameProbeSignature(hierarchy, plan.OriginalHierarchyProbe))
            {
                throw new IOException("重试 unload 前原始四阶段签名已变化。", exception);
            }

            if (plan.PersistentDefaults is not null)
            {
                await ValidatePlanAfterDefaultsWriteAsync(plan, cancellationToken).ConfigureAwait(false);
            }

            current = await CaptureAsync(current.InstanceId, cancellationToken).ConfigureAwait(false);
            if (!current.Fingerprint.Equals(plan.OriginalInstance.Fingerprint, StringComparison.Ordinal))
            {
                throw new IOException("重试 unload 前实例身份或配置已漂移。", exception);
            }

            await client.UnloadAsync(current.InstanceId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>加载补丁实例；瞬态失败时若同源已恰有一个等价实例则视为“只丢了响应”，否则复核全部前置后重发 load。</summary>
    private async Task<LmStudioLoadResponse> LoadPatchedWithReconciliationAsync(LmStudioTemplateRepairPlan plan, LmStudioPromptTemplateConfiguration? promptTemplate, CancellationToken cancellationToken)
    {
        LmStudioLoadedInstanceSnapshot original = plan.OriginalInstance;
        try
        {
            return await client.LoadAsync(original.SourceModelKey, original.LoadConfiguration, promptTemplate, PositiveTtl(original.RemainingTtlSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SwitchRetryBudget.IsTransient(exception, cancellationToken))
        {
            if (!await SwitchRetryBudget.TryConsumeAsync(cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
            ModelProfile[] sameSource = models.Where(model => model.IsLoaded == true && string.Equals(model.SourceModelKey, original.SourceModelKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameSource.Length == 1)
            {
                LmStudioLoadedInstanceSnapshot current = await CaptureAsync(sameSource[0].Id, cancellationToken).ConfigureAwait(false);
                ValidateReloadedSnapshot(original, current);
                // 操作可能只是丢了 HTTP 响应：调用方仍会执行完整层级与持久 defaults 检查
                return new LmStudioLoadResponse(current.InstanceId, "loaded", current.LoadConfiguration);
            }

            if (sameSource.Length != 0)
            {
                throw new IOException("load 响应异常后出现多个同源实例；不会重发 load。", exception);
            }

            EnsureNativeSourceIdentityUsable(original.SourceModelKey);
            await ValidateLoadTargetCurrentAsync(original, cancellationToken).ConfigureAwait(false);
            await ValidateJournalGgufAsync(CreateRecord(plan), cancellationToken).ConfigureAwait(false);
            if (plan.PersistentDefaults is not null)
            {
                await LmStudioPerModelDefaultsStore.VerifyAppliedAsync(plan.PersistentDefaults, cancellationToken).ConfigureAwait(false);
            }

            await EnsureSourceAbsentAsync(original.SourceModelKey, cancellationToken).ConfigureAwait(false);
            return await client.LoadAsync(original.SourceModelKey, original.LoadConfiguration, promptTemplate, PositiveTtl(original.RemainingTtlSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>执行四阶段探测（瞬态可重试一次，重试前后都复核实例指纹未变）。</summary>
    private async Task<CodexInstructionHierarchyProbeResult> ProbeWithRetryAsync(LmStudioLoadedInstanceSnapshot expected, CancellationToken cancellationToken)
    {
        var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync(expected.InstanceId, cancellationToken).ConfigureAwait(false);
        if (SwitchRetryBudget.IsTransient(result) && await SwitchRetryBudget.TryConsumeAsync(cancellationToken).ConfigureAwait(false))
        {
            LmStudioLoadedInstanceSnapshot current = await CaptureAsync(expected.InstanceId, cancellationToken).ConfigureAwait(false);
            if (!current.Fingerprint.Equals(expected.Fingerprint, StringComparison.Ordinal))
            {
                throw new IOException("四阶段重试前实例身份或完整加载配置发生变化。");
            }

            result = await probe.ProbeAsync(expected.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        LmStudioLoadedInstanceSnapshot after = await CaptureAsync(expected.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!after.Fingerprint.Equals(expected.Fingerprint, StringComparison.Ordinal))
        {
            throw new IOException("四阶段检测期间实例身份或完整加载配置发生变化。");
        }

        return result;
    }

    /// <summary>按计划执行回滚（读取或重建事务记录后转核心回滚）。</summary>
    public async Task<LmStudioRollbackResult> RollbackAsync(LmStudioTemplateRepairPlan plan, string? patchedInstanceId, CancellationToken cancellationToken = default)
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

    /// <summary>评估并执行事务恢复（两步合一的便捷入口）。</summary>
    public async Task<LmStudioRollbackResult> RecoverAsync(LmStudioTemplateTransactionRecord transaction, CancellationToken cancellationToken = default)
    {
        LmStudioRecoveryAssessment assessment = await AssessRecoveryAsync(transaction, cancellationToken).ConfigureAwait(false);
        return await RecoverAsync(transaction, assessment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 评估恢复策略：重验 journal 的 GGUF 与运行时证据、采集 defaults 指纹与同源实例，
    /// 逐个候选探测后归并为四种处置（已恢复 / 重载原实例 / 卸载已知补丁后重载 / 歧义阻断），
    /// 并给出评估状态指纹供执行前复核。
    /// </summary>
    public async Task<LmStudioRecoveryAssessment> AssessRecoveryAsync(LmStudioTemplateTransactionRecord transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnsureControllerMatches(transaction.OriginalInstance);
        GgufChatTemplateAnalysis recoveryAnalysis = await ValidateJournalGgufAsync(transaction, cancellationToken).ConfigureAwait(false);
        await ValidateOriginalRuntimeEvidenceAsync(transaction, recoveryAnalysis, cancellationToken).ConfigureAwait(false);
        FileFingerprint? currentDefaultsFingerprint = await CaptureRecoveryDefaultsFingerprintAsync(transaction, cancellationToken).ConfigureAwait(false);
        bool requiresPersistenceRecovery = transaction.SchemaVersion >= 4 &&
            transaction.PersistenceStage >= LmStudioPersistenceStage.BackupVerified &&
            transaction.PersistenceStage != LmStudioPersistenceStage.Restored &&
            currentDefaultsFingerprint is not null &&
            transaction.OriginalDefaultsFingerprint is not null &&
            !FileFingerprintService.Matches(transaction.OriginalDefaultsFingerprint, currentDefaultsFingerprint);

        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        EnsureNativeSourceIdentityUsable(transaction.OriginalInstance.SourceModelKey);
        ModelProfile[] sameSource = models.Where(model =>
            model.IsLoaded == true &&
            string.Equals(model.SourceModelKey, transaction.OriginalInstance.SourceModelKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        List<LmStudioRecoveryCandidate> candidates = [];
        var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
        foreach (ModelProfile model in sameSource.OrderBy(model => model.Id, StringComparer.Ordinal))
        {
            LmStudioLoadedInstanceSnapshot snapshot = CreateSnapshot(model);
            bool matchesOriginal = SnapshotsReloadEquivalent(transaction.OriginalInstance, snapshot);
            CodexInstructionHierarchyProbeResult? hierarchy = matchesOriginal ? await probe.ProbeAsync(snapshot.InstanceId, cancellationToken).ConfigureAwait(false) : null;
            bool reproducesOriginal = hierarchy is not null && ReproducesOriginalRuntimeSignature(hierarchy, transaction);
            candidates.Add(new LmStudioRecoveryCandidate(snapshot, matchesOriginal, hierarchy, reproducesOriginal));
        }

        string? knownPatchId = transaction.PatchedInstanceId ?? transaction.CandidateInstanceId;
        bool knownPatchPresent = !string.IsNullOrWhiteSpace(knownPatchId) &&
            candidates.Any(candidate => candidate.Snapshot.InstanceId.Equals(knownPatchId, StringComparison.Ordinal));
        LmStudioRecoveryCandidate? alreadyRestored = candidates.Count == 1 ? candidates[0] : null;
        bool originalStageEvidence = HasOriginalRecoveryEvidence(transaction);
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
            detail = requiresPersistenceRecovery
                ? "当前唯一实例已复现原始运行时签名，但持久 defaults 尚未恢复；确认后会先验证 DPAPI 备份并恢复管理器拥有的 Prompt Template 字段，再关闭 journal，不执行模型 unload/load。"
                : transaction.SchemaVersion < 3
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
            bool patchStageEvidence = transaction.State is LmStudioTemplateTransactionState.OriginalUnloaded or LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified ||
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

        string fingerprint = ComputeRecoveryAssessmentFingerprint(transaction, candidates, disposition, instanceToUnload, currentDefaultsFingerprint);
        return new LmStudioRecoveryAssessment(
            transaction.TransactionId,
            disposition,
            candidates,
            instanceToUnload,
            disposition is LmStudioRecoveryDisposition.LoadOriginal or LmStudioRecoveryDisposition.UnloadKnownPatchAndLoadOriginal,
            transaction.SchemaVersion < 3,
            fingerprint,
            detail,
            currentDefaultsFingerprint,
            requiresPersistenceRecovery);
    }

    /// <summary>
    /// 按评估结果执行恢复：重读记录并复核评估指纹未变；按需先恢复持久 defaults，
    /// 已恢复则直接关闭 journal，否则进入核心回滚（卸载补丁实例并重载原实例）。
    /// </summary>
    public async Task<LmStudioRollbackResult> RecoverAsync(LmStudioTemplateTransactionRecord transaction, LmStudioRecoveryAssessment assessment, CancellationToken cancellationToken = default)
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

            if (currentRecord.SchemaVersion >= 4 &&
                currentRecord.PersistenceStage >= LmStudioPersistenceStage.BackupVerified &&
                currentRecord.PersistenceStage != LmStudioPersistenceStage.Restored)
            {
                LmStudioPerModelDefaultsStore defaultsStore = perModelDefaultsStore ?? throw new InvalidOperationException("schema-v4 恢复缺少 per-model defaults 存储服务。");
                GgufChatTemplateAnalysis analysis = await ValidateJournalGgufAsync(currentRecord, cancellationToken).ConfigureAwait(false);
                LmStudioDefaultsRestoreResult restore = await defaultsStore.RestoreFromTransactionAsync(currentRecord, analysis, cancellationToken).ConfigureAwait(false);
                if (!restore.Succeeded)
                {
                    LmStudioTemplateTransactionRecord blocked = currentRecord with
                    {
                        State = LmStudioTemplateTransactionState.RecoveryBlocked,
                        UpdatedAt = DateTimeOffset.Now,
                        Detail = restore.Detail,
                        FailureStage = LmStudioLifecycleStage.RestoreDefaults,
                        LastRecoveryFailureStage = LmStudioLifecycleStage.RestoreDefaults,
                        PersistenceStage = LmStudioPersistenceStage.RecoveryBlocked,
                        RecoveryAttemptCount = attempt,
                    };
                    await transactions.WriteAsync(blocked, cancellationToken).ConfigureAwait(false);
                    return new LmStudioRollbackResult(false, restore.Detail, null, transactions.GetPath(transaction.TransactionId));
                }

                currentRecord = await UpdatePersistenceRecordAsync(currentRecord, LmStudioPersistenceStage.Restored, restore.Detail, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 标记事务完成：仅 PatchedAndVerified 可完成；schema-v4 还会复核 LM Studio 版本、
    /// 持久 defaults 目标与当前补丁实例快照后才落 Completed。
    /// </summary>
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

            if (record.SchemaVersion >= 4)
            {
                if (perModelDefaultsStore is null || string.IsNullOrWhiteSpace(record.PatchedInstanceId) ||
                    string.IsNullOrWhiteSpace(record.LmStudioVersion) ||
                    !record.LmStudioVersion.Equals(lmStudioVersionProvider(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("完成 schema-v4 事务前无法重新确认 LM Studio 版本、持久存储服务或 patched instance。");
                }

                await LmStudioPerModelDefaultsStore.VerifyTransactionTargetAsync(record, cancellationToken).ConfigureAwait(false);
                LmStudioLoadedInstanceSnapshot patched = await CaptureAsync(record.PatchedInstanceId, cancellationToken).ConfigureAwait(false);
                ValidateReloadedSnapshot(record.OriginalInstance, patched);
                record = await UpdatePersistenceRecordAsync(record, LmStudioPersistenceStage.PersistentDefaultVerified, "Codex 配置提交后已再次验证持久 defaults 和当前 instance。", cancellationToken).ConfigureAwait(false);
            }

            await UpdateRecordAsync(
                record,
                LmStudioTemplateTransactionState.Completed,
                record.PatchedInstanceId,
                record.SchemaVersion >= 4
                    ? "Codex 配置切换已提交；Completed / PersistentDefaultVerified。"
                    : "Codex 配置切换已提交；保留补丁实例。",
                cancellationToken).ConfigureAwait(false);
            LogSafely(() => logger.Info($"LM Studio template transaction completed: id={transactionId:N}, instance={record.PatchedInstanceId ?? "unknown"}"));
            ReportProgress("Completed：Codex 配置已提交，保留补丁实例");
        }
        finally
        {
            ReleaseLifecycleLease(transactionId);
        }
    }

    /// <summary>
    /// 核心回滚：恢复持久 defaults（如需）→ 卸载已知/疑似补丁实例 → 按原变体与完整配置重载原实例
    /// → 验证快照与原始四阶段签名 → RolledBack；失败记录 RollbackFailed 与失败证据。
    /// </summary>
    private async Task<LmStudioRollbackResult> RollbackCoreAsync(LmStudioLoadedInstanceSnapshot original, Guid transactionId, string? patchedInstanceId, LmStudioTemplateTransactionRecord record, CancellationToken cancellationToken, bool? patchLoadStarted = null)
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
            if (record.SchemaVersion >= 4 && record.PersistenceStage >= LmStudioPersistenceStage.BackupVerified && record.PersistenceStage != LmStudioPersistenceStage.Restored)
            {
                LmStudioPerModelDefaultsStore defaultsStore = perModelDefaultsStore ?? throw new InvalidOperationException("schema-v4 恢复缺少 per-model defaults 存储服务。");
                failureStage = LmStudioLifecycleStage.RestoreDefaults;
                ReportProgress("先恢复 LM Studio per-model Prompt Template 默认值");
                LmStudioDefaultsRestoreResult defaultsRestore = await defaultsStore.RestoreFromTransactionAsync(record, rollbackAnalysis, cancellationToken).ConfigureAwait(false);
                if (!defaultsRestore.Succeeded)
                {
                    LmStudioTemplateTransactionRecord blocked = record with
                    {
                        State = LmStudioTemplateTransactionState.RecoveryBlocked,
                        UpdatedAt = DateTimeOffset.Now,
                        Detail = defaultsRestore.Detail,
                        FailureStage = LmStudioLifecycleStage.RestoreDefaults,
                        LastRecoveryFailureStage = LmStudioLifecycleStage.RestoreDefaults,
                        PersistenceStage = LmStudioPersistenceStage.RecoveryBlocked,
                    };
                    await transactions.WriteAsync(blocked, CancellationToken.None).ConfigureAwait(false);
                    ReportProgress("RecoveryBlocked：持久 Prompt Template 已被外部修改，未覆盖也未卸载实例");
                    return new LmStudioRollbackResult(false, defaultsRestore.Detail, null, transactionPath);
                }

                record = await UpdatePersistenceRecordAsync(record, LmStudioPersistenceStage.Restored, defaultsRestore.Detail, cancellationToken).ConfigureAwait(false);
            }

            LmStudioPromptTemplateConfiguration? originalPromptTemplate = RecreateOriginalRuntimeTemplate(record, rollbackAnalysis);
            IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
            EnsureNativeSourceIdentityUsable(original.SourceModelKey);

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
                    HasOriginalRecoveryEvidence(record);
                if (matchesOriginal && canAdoptExisting && await ReproducesOriginalRuntimeAsync(candidate.InstanceId, record, cancellationToken).ConfigureAwait(false))
                {
                    await UpdateRecordAsync(record, LmStudioTemplateTransactionState.RolledBack, null, "检测到已恢复的原实例。", cancellationToken).ConfigureAwait(false);
                    return new LmStudioRollbackResult(true, "原实例已经恢复，并复现原始 Prompt Template 失败签名。", candidate, transactionPath);
                }

                // load 后未拿到 instance_id 的情况：唯一同源实例大概率就是补丁实例，先卸载再重载原实例
                bool patchLoadWasLastStable = patchLoadStarted != false && effectivePatchedInstanceId is null &&
                    record.FailureStage == LmStudioLifecycleStage.LoadPatched &&
                    (record.State == LmStudioTemplateTransactionState.OriginalUnloaded ||
                      record.LastStableState == LmStudioTemplateTransactionState.OriginalUnloaded) &&
                    !HasOriginalRecoveryEvidence(record) && matchesOriginal;
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
                load = await client.LoadAsync(original.SourceModelKey, original.LoadConfiguration, originalPromptTemplate, PositiveTtl(original.RemainingTtlSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // load 响应异常但 native 状态已证明原实例恢复等价 → 视为回滚成功
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
            LogSafely(() => logger.Info($"LM Studio template transaction rolled back: id={transactionId:N}, restored={restored.InstanceId}"));
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

            LogSafely(() => logger.LogError($"LM Studio template rollback failed: id={transactionId:N}", exception));
            ReportProgress("RollbackFailed：自动恢复已停止");
            return new LmStudioRollbackResult(false, detail, null, transactionPath);
        }
    }

    /// <summary>重验 journal 记录的 GGUF 文件（存在性、文件名、长度、修改时间、版本与模板哈希）。</summary>
    private async Task<GgufChatTemplateAnalysis> ValidateJournalGgufAsync(LmStudioTemplateTransactionRecord record, CancellationToken cancellationToken)
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

    /// <summary>变更前的全量计划校验（瞬态失败时整体重试，绝不只重试单个请求）。</summary>
    private async Task<LmStudioLoadedInstanceSnapshot> ValidatePlanBeforeMutationAsync(LmStudioTemplateRepairPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            return await ValidatePlanBeforeMutationCoreAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SwitchRetryBudget.IsTransient(exception, cancellationToken))
        {
            if (!await SwitchRetryBudget.TryConsumeAsync(cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            // 重试整个“变更前校验”，绝不只重试失败的那个请求
            return await ValidatePlanBeforeMutationCoreAsync(plan, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>变更前校验核心：实例指纹与 TTL、加载目标、schema 预检、四阶段签名、GGUF 与预览结果、运行时与持久化证据。</summary>
    private async Task<LmStudioLoadedInstanceSnapshot> ValidatePlanBeforeMutationCoreAsync(LmStudioTemplateRepairPlan plan, CancellationToken cancellationToken)
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
        if (plan.PersistentDefaults is null)
        {
            await client.ProbePromptTemplateSchemaAsync(cancellationToken).ConfigureAwait(false);
        }

        CodexInstructionHierarchyProbeResult currentHierarchy = await ProbeWithRetryAsync(current, cancellationToken).ConfigureAwait(false);
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
        if (plan.PersistentDefaults is not null)
        {
            await ValidatePersistentPlanCurrentAsync(plan, current, analysis, cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>校验持久化计划在预览后仍然有效：版本一致、concrete GGUF 身份一致、重建计划与原计划等价。</summary>
    private async Task ValidatePersistentPlanCurrentAsync(LmStudioTemplateRepairPlan plan, LmStudioLoadedInstanceSnapshot current, GgufChatTemplateAnalysis analysis, CancellationToken cancellationToken)
    {
        LmStudioPerModelDefaultsPlan expected = plan.PersistentDefaults
            ?? throw new InvalidOperationException("持久化计划缺失。");
        LmStudioPerModelDefaultsStore defaultsStore = perModelDefaultsStore
            ?? throw new InvalidOperationException("持久化计划缺少 per-model defaults 存储服务。");
        ILmStudioModelFileLocator locator = modelFileLocator
            ?? throw new InvalidOperationException("持久化计划缺少 concrete GGUF 身份定位服务。");
        string? currentVersion = lmStudioVersionProvider();
        if (string.IsNullOrWhiteSpace(currentVersion) || !currentVersion.Equals(plan.LmStudioVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("LM Studio 版本在 Preview 后发生变化或已无法确认；任何 defaults 写入和 unload 均已阻断。");
        }

        LmStudioModelFileResolution resolution = await ResolveCurrentModelFileAsync(locator, current, cancellationToken).ConfigureAwait(false);
        if (!Path.GetFullPath(resolution.FilePath).Equals(Path.GetFullPath(plan.ModelFile.FilePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolution.ConcreteModelIdentifier, expected.ConcreteModelIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("lms ps 的 concrete GGUF 身份在 Preview 后发生变化；任何 defaults 写入和 unload 均已阻断。");
        }

        LmStudioPerModelDefaultsPlan refreshed = await defaultsStore.CreatePlanAsync(endpoint, currentVersion, resolution, analysis, plan.TemplatePreview, plan.OriginalRuntimeTemplate, cancellationToken).ConfigureAwait(false);
        if (!PersistentPlansEquivalent(expected, refreshed))
        {
            throw new IOException("LM Studio per-model defaults 在 Preview 后发生变化；任何文件写入和 unload 均已阻断。");
        }
    }

    /// <summary>写入 defaults 后的复核：版本、实例指纹、concrete 身份、GGUF 模板哈希与已落盘 defaults。</summary>
    private async Task ValidatePlanAfterDefaultsWriteAsync(LmStudioTemplateRepairPlan plan, CancellationToken cancellationToken)
    {
        LmStudioPerModelDefaultsPlan persistent = plan.PersistentDefaults
            ?? throw new InvalidOperationException("持久化计划缺失。");
        ILmStudioModelFileLocator locator = modelFileLocator
            ?? throw new InvalidOperationException("持久化计划缺少 concrete GGUF 身份定位服务。");
        string? currentVersion = lmStudioVersionProvider();
        if (string.IsNullOrWhiteSpace(currentVersion) || !currentVersion.Equals(plan.LmStudioVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("写入 defaults 后 LM Studio 版本发生变化；事务将恢复原文件且不会 unload。");
        }

        LmStudioLoadedInstanceSnapshot current = await CaptureAsync(plan.OriginalInstance.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!current.Fingerprint.Equals(plan.OriginalInstance.Fingerprint, StringComparison.Ordinal))
        {
            throw new IOException("写入 defaults 后 native loaded instance 发生变化；事务将恢复原文件且不会继续 unload。");
        }

        LmStudioModelFileResolution resolution = await ResolveCurrentModelFileAsync(locator, current, cancellationToken).ConfigureAwait(false);
        if (!Path.GetFullPath(resolution.FilePath).Equals(Path.GetFullPath(plan.ModelFile.FilePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolution.ConcreteModelIdentifier, persistent.ConcreteModelIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("写入 defaults 后 lms ps 的 concrete GGUF 身份发生变化；事务将回滚。");
        }

        GgufChatTemplateAnalysis analysis = await ggufReader.ReadAsync(plan.GgufAnalysis.FilePath, cancellationToken).ConfigureAwait(false);
        if (!analysis.TemplateSha256.Equals(plan.GgufAnalysis.TemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("写入 defaults 后 GGUF Prompt Template SHA 发生变化；事务将回滚。");
        }

        await LmStudioPerModelDefaultsStore.VerifyAppliedAsync(persistent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>为当前实例重新定位 concrete GGUF 身份，失败即抛 IOException。</summary>
    private async Task<LmStudioModelFileResolution> ResolveCurrentModelFileAsync(ILmStudioModelFileLocator locator, LmStudioLoadedInstanceSnapshot snapshot, CancellationToken cancellationToken)
    {
        ModelProfile profile = CreateLocatorProfile(snapshot);
        LmStudioModelFileResolutionAttempt attempt = await locator.ResolveAsync(profile, endpoint, cancellationToken).ConfigureAwait(false);
        if (!attempt.Succeeded || attempt.Resolution is null)
        {
            throw new IOException($"无法重新证明当前 loaded instance 的 concrete GGUF 身份：{attempt.Diagnostic}");
        }

        return attempt.Resolution;
    }

    /// <summary>把实例快照转换为定位器所需的模型档案。</summary>
    private static ModelProfile CreateLocatorProfile(LmStudioLoadedInstanceSnapshot snapshot) => new(
        snapshot.InstanceId,
        snapshot.InstanceId,
        ProviderKind.LmStudio,
        Quantization: snapshot.Quantization,
        Parameters: snapshot.Parameters,
        IsLoaded: true,
        MaxContextLength: snapshot.MaxContextLength,
        LoadedContextLength: snapshot.LoadConfiguration.ContextLength,
        Source: "/api/v1/models",
        LoadedInstanceId: snapshot.InstanceId,
        Architecture: snapshot.Architecture,
        ModelType: snapshot.ModelType,
        SourceModelKey: snapshot.SourceModelKey,
        SelectedVariant: snapshot.SelectedVariant,
        LoadedConfiguration: snapshot.LoadConfiguration,
        RemainingTtlSeconds: snapshot.RemainingTtlSeconds,
        AvailableVariants: snapshot.LoadTarget?.AvailableVariants,
        Format: snapshot.LoadTarget?.Format);

    /// <summary>逐字段比较两份持久化计划是否等价（含原始/候选字节逐字节一致）。</summary>
    private static bool PersistentPlansEquivalent(LmStudioPerModelDefaultsPlan expected, LmStudioPerModelDefaultsPlan actual) =>
        expected.ConcreteModelIdentifier.Equals(actual.ConcreteModelIdentifier, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(expected.FilePath).Equals(Path.GetFullPath(actual.FilePath), StringComparison.OrdinalIgnoreCase) &&
        expected.LmStudioVersion.Equals(actual.LmStudioVersion, StringComparison.OrdinalIgnoreCase) &&
        FileFingerprintService.Matches(expected.OriginalFingerprint, actual.OriginalFingerprint) &&
        expected.CandidateFingerprint.Sha256.Equals(actual.CandidateFingerprint.Sha256, StringComparison.OrdinalIgnoreCase) &&
        expected.OriginalFieldState == actual.OriginalFieldState &&
        SameOptional(expected.OriginalRuleVersion, actual.OriginalRuleVersion) &&
        SameOptional(expected.OriginalTemplateSha256, actual.OriginalTemplateSha256) &&
        expected.TargetRuleVersion.Equals(actual.TargetRuleVersion, StringComparison.Ordinal) &&
        expected.TargetTemplateSha256.Equals(actual.TargetTemplateSha256, StringComparison.OrdinalIgnoreCase) &&
        expected.Mutation == actual.Mutation &&
        expected.OriginalBytes.AsSpan().SequenceEqual(actual.OriginalBytes) &&
        expected.CandidateBytes.AsSpan().SequenceEqual(actual.CandidateBytes);

    /// <summary>校验计划的原运行时模板来源：内置模式不得带规则字段；规则模式必须有完整的 completed 事务佐证且与当前实例/GGUF 全量一致。</summary>
    private async Task ValidateRuntimeProvenanceAsync(LmStudioTemplateRepairPlan plan, LmStudioLoadedInstanceSnapshot current, GgufChatTemplateAnalysis analysis, CancellationToken cancellationToken)
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

    /// <summary>比较两次四阶段探测的行为签名（四步结果 + 失败码）是否一致。</summary>
    private static bool SameProbeSignature(CodexInstructionHierarchyProbeResult left, CodexInstructionHierarchyProbeResult right) =>
        left.Control == right.Control &&
        left.LeadingDeveloper == right.LeadingDeveloper &&
        left.ConversationControl == right.ConversationControl &&
        left.ContinuationDeveloper == right.ContinuationDeveloper &&
        string.Equals(left.FailureCode, right.FailureCode, StringComparison.Ordinal);

    /// <summary>确保没有 Codex/ChatGPT 进程在运行（生命周期操作前置条件）。</summary>
    private async Task EnsureCodexClosedAsync(CancellationToken cancellationToken)
    {
        CodexEnvironmentInfo environment = await runtimeProbe.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (environment.IsRunning)
        {
            throw new InvalidOperationException("应用兼容模板前必须完全关闭 Codex Desktop、CLI 与 helper 进程。");
        }
    }

    /// <summary>尝试捕获实例快照；不存在返回 null。</summary>
    private async Task<LmStudioLoadedInstanceSnapshot?> TryCaptureAsync(string instanceId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelProfile? model = models.SingleOrDefault(model => model.IsLoaded == true && model.Id.Equals(instanceId, StringComparison.Ordinal));
        return model is null ? null : CreateSnapshot(model);
    }

    /// <summary>确认指定源模型当前没有任何已加载实例。</summary>
    private async Task EnsureSourceAbsentAsync(string sourceModelKey, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(cancellationToken).ConfigureAwait(false);
        EnsureNativeSourceIdentityUsable(sourceModelKey);
        if (models.Any(model =>
            model.IsLoaded == true &&
            string.Equals(model.SourceModelKey, sourceModelKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("卸载确认期间出现同一源模型的其他 loaded instance；未发送补丁 load 请求，必须刷新后重试。");
        }
    }

    /// <summary>复核加载目标（源模型）的身份与变体清单仍与快照一致。</summary>
    private async Task<LmStudioLoadTarget> ValidateLoadTargetCurrentAsync(LmStudioLoadedInstanceSnapshot original, CancellationToken cancellationToken)
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

    /// <summary>由 native 模型档案构造带指纹的实例快照（要求加载状态与上下文数据完整）。</summary>
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

    /// <summary>构造加载目标：校验变体清单自洽（多变体必须报告选中项且在清单内），并以规范化 JSON 哈希作指纹。</summary>
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

    /// <summary>计算实例快照的规范化指纹（端点 + 身份 + 加载配置 + 加载目标）。</summary>
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

    /// <summary>计算恢复评估的状态指纹（事务字段 + defaults 指纹 + 候选与处置结论的规范化哈希）。</summary>
    private static string ComputeRecoveryAssessmentFingerprint(LmStudioTemplateTransactionRecord transaction, IReadOnlyList<LmStudioRecoveryCandidate> candidates, LmStudioRecoveryDisposition disposition, string? instanceToUnload, FileFingerprint? currentDefaultsFingerprint)
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
            transaction.ConcreteModelIdentifier,
            transaction.PerModelDefaultsPath,
            transaction.OriginalDefaultsFingerprint,
            transaction.OriginalPersistentTemplateState,
            transaction.OriginalPersistentRuleVersion,
            transaction.OriginalPersistentTemplateSha256,
            transaction.TargetPersistentRuleVersion,
            transaction.TargetPersistentTemplateSha256,
            transaction.CandidateDefaultsSha256,
            transaction.EncryptedDefaultsBackupPath,
            transaction.DefaultsBackupPlaintextSha256,
            transaction.PersistenceStage,
            transaction.LmStudioVersion,
            CurrentDefaultsFingerprint = currentDefaultsFingerprint,
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

    /// <summary>采集恢复所需的 defaults 指纹：schema-v3 及以下返回 null；v4 校验路径一致后取当前指纹。</summary>
    private async Task<FileFingerprint?> CaptureRecoveryDefaultsFingerprintAsync(LmStudioTemplateTransactionRecord transaction, CancellationToken cancellationToken)
    {
        if (transaction.SchemaVersion < 4)
        {
            return null;
        }

        LmStudioPerModelDefaultsStore defaultsStore = perModelDefaultsStore
            ?? throw new InvalidOperationException("schema-v4 恢复评估缺少 per-model defaults 存储服务。");
        if (string.IsNullOrWhiteSpace(transaction.ConcreteModelIdentifier) || string.IsNullOrWhiteSpace(transaction.PerModelDefaultsPath))
        {
            throw new InvalidDataException("schema-v4 恢复事务缺少 concrete model identifier 或 defaults 路径。");
        }

        string expectedPath = defaultsStore.GetDefaultsPath(transaction.ConcreteModelIdentifier);
        if (!Path.GetFullPath(transaction.PerModelDefaultsPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("schema-v4 恢复事务的 defaults 路径与 concrete model identifier 不一致。");
        }

        return await FileFingerprintService.CaptureAsync(expectedPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验重载后的快照与原实例等价（身份、变体、加载配置、TTL 合理性）。</summary>
    private static void ValidateReloadedSnapshot(LmStudioLoadedInstanceSnapshot original, LmStudioLoadedInstanceSnapshot reloaded)
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
            // 重载后的 TTL 不应低于“原 TTL − 操作耗时 − 30 秒容差”
            int elapsedSeconds = (int)Math.Ceiling(Math.Max(0, (DateTimeOffset.Now - original.CapturedAt).TotalSeconds));
            int minimumReasonableTtl = Math.Max(1, original.RemainingTtlSeconds.Value - elapsedSeconds - 30);
            if (reloaded.RemainingTtlSeconds < minimumReasonableTtl)
            {
                throw new InvalidDataException("重载后的剩余 TTL 下降幅度超过本次操作耗时与安全容差。");
            }
        }
    }

    /// <summary>判断两份快照是否重载等价（校验异常视为不等价）。</summary>
    private static bool SnapshotsReloadEquivalent(LmStudioLoadedInstanceSnapshot original, LmStudioLoadedInstanceSnapshot candidate)
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

    /// <summary>按事务记录确定性重建原运行时模板：内置模式（v4 用 GGUF 原文，v3 以下不传）；规则模式重建 v2 模板。</summary>
    private LmStudioPromptTemplateConfiguration? RecreateOriginalRuntimeTemplate(LmStudioTemplateTransactionRecord record, GgufChatTemplateAnalysis analysis)
    {
        if (record.OriginalRuntimeTemplateMode == LmStudioRuntimeTemplateMode.BuiltIn)
        {
            return record.SchemaVersion >= 4
                ? new LmStudioPromptTemplateConfiguration("jinja", analysis.ChatTemplate, [])
                : null;
        }

        if (record.OriginalRuntimeTemplateMode != LmStudioRuntimeTemplateMode.ManagerRule ||
            string.IsNullOrWhiteSpace(record.OriginalRuntimeRuleVersion) ||
            string.IsNullOrWhiteSpace(record.OriginalRuntimeTemplateSha256) ||
            record.OriginalRuntimeEvidenceTransactionId is null)
        {
            throw new InvalidDataException("事务缺少可确定性恢复的原运行时模板 provenance；未执行生命周期操作。");
        }

        string template = templateRepair.RecreateKnownTemplate(analysis, record.OriginalRuntimeRuleVersion, record.OriginalRuntimeTemplateSha256);
        return new LmStudioPromptTemplateConfiguration("jinja", template, []);
    }

    /// <summary>校验事务记录的原运行时管理器模板有完整且一致的 completed 佐证。</summary>
    private async Task ValidateOriginalRuntimeEvidenceAsync(LmStudioTemplateTransactionRecord record, GgufChatTemplateAnalysis analysis, CancellationToken cancellationToken)
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

    /// <summary>探测指定实例并判断其是否复现事务记录的原始运行时签名。</summary>
    private async Task<bool> ReproducesOriginalRuntimeAsync(string instanceId, LmStudioTemplateTransactionRecord record, CancellationToken cancellationToken)
    {
        var probe = new CodexInstructionHierarchyProbe(httpClient, endpoint, tokenProvider);
        CodexInstructionHierarchyProbeResult result = await probe.ProbeAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return ReproducesOriginalRuntimeSignature(result, record);
    }

    /// <summary>
    /// 原始运行时签名判定：v3+ 直接比对记录的四阶段签名；
    /// v2 规则模式比对“前三步通过、续轮失败”签名；内置模式比对“控制通过、层级失败、失败码一致”。
    /// </summary>
    private static bool ReproducesOriginalRuntimeSignature(CodexInstructionHierarchyProbeResult result, LmStudioTemplateTransactionRecord record)
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

    /// <summary>尝试和解“恢复 load 响应异常”的场景：同源恰有一个等价且复现原始签名的实例则返回它，否则 null。</summary>
    private async Task<LmStudioLoadedInstanceSnapshot?> TryReconcileRestoredInstanceAsync(LmStudioLoadedInstanceSnapshot original, LmStudioTemplateTransactionRecord record, CancellationToken cancellationToken)
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

    /// <summary>可选字符串等值比较（同空同有，有值时忽略大小写相等）。</summary>
    private static bool SameOptional(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) ||
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    /// <summary>把 TTL 归一为“正数或 null”。</summary>
    private static int? PositiveTtl(int? ttl) => ttl is > 0 ? ttl : null;

    /// <summary>确保控制器端点与事务预览端点一致。</summary>
    private void EnsureControllerMatches(LmStudioLoadedInstanceSnapshot snapshot)
    {
        if (!endpoint.AbsoluteUri.TrimEnd('/').Equals(snapshot.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LM Studio endpoint 与事务预览不一致。");
        }
    }

    /// <summary>由修复计划构造初始 Prepared 事务记录（schema v3 无持久化，v4 带完整持久化证据）。</summary>
    private static LmStudioTemplateTransactionRecord CreateRecord(LmStudioTemplateRepairPlan plan) => new(
        plan.PersistentDefaults is null ? 3 : 4,
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
        OriginalHierarchyProbe: plan.OriginalHierarchyProbe,
        ConcreteModelIdentifier: plan.PersistentDefaults?.ConcreteModelIdentifier,
        PerModelDefaultsPath: plan.PersistentDefaults?.FilePath,
        OriginalDefaultsFingerprint: plan.PersistentDefaults?.OriginalFingerprint,
        OriginalPersistentTemplateState: plan.PersistentDefaults?.OriginalFieldState,
        OriginalPersistentRuleVersion: plan.PersistentDefaults?.OriginalRuleVersion,
        OriginalPersistentTemplateSha256: plan.PersistentDefaults?.OriginalTemplateSha256,
        TargetPersistentRuleVersion: plan.PersistentDefaults?.TargetRuleVersion,
        TargetPersistentTemplateSha256: plan.PersistentDefaults?.TargetTemplateSha256,
        CandidateDefaultsSha256: plan.PersistentDefaults?.CandidateFingerprint.Sha256,
        PersistenceStage: plan.PersistentDefaults is null ? LmStudioPersistenceStage.None : LmStudioPersistenceStage.Prepared,
        LmStudioVersion: plan.LmStudioVersion);

    /// <summary>
    /// 记录应用失败的证据：在 5 秒预算内重读同源实例清单，推断“load 已发生但未拿到 ID”的候选实例，
    /// 更新 journal 后返回；证据采集失败不影响主异常。
    /// </summary>
    private async Task<LmStudioTemplateTransactionRecord> RecordApplyFailureEvidenceAsync(LmStudioTemplateTransactionRecord record, string sourceModelKey, string? patchedInstanceId, bool patchedLoadStarted, LmStudioLifecycleStage failureStage, Exception exception)
    {
        LmStudioTemplateTransactionRecord updated = record with
        {
            FailureStage = failureStage,
            LastApiFailure = exception is LmStudioApiException apiException ? apiException.Failure : record.LastApiFailure,
        };
        try
        {
            using var evidenceTimeout = new CancellationTokenSource(FailureEvidenceTimeout);
            IReadOnlyList<ModelProfile> models = await client.DiscoverNativeModelsAsync(evidenceTimeout.Token).ConfigureAwait(false);
            string[] after = models
                .Where(model => model.IsLoaded == true && string.Equals(model.SourceModelKey, sourceModelKey, StringComparison.OrdinalIgnoreCase))
                .Select(model => model.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string? candidate = patchedLoadStarted && string.IsNullOrWhiteSpace(patchedInstanceId) && after.Length == 1 &&
                SnapshotsReloadEquivalent(record.OriginalInstance, CreateSnapshot(models.Single(model => model.IsLoaded == true && model.Id == after[0])))
                ? after[0]
                : record.CandidateInstanceId;
            updated = updated with
            {
                SchemaVersion = Math.Max(record.SchemaVersion, 2),
                UpdatedAt = DateTimeOffset.Now,
                LoadModelKey = record.LoadModelKey ?? sourceModelKey,
                LastStableState = record.LastStableState ?? record.State,
                SameSourceInstanceIdsBeforeLoad = record.SameSourceInstanceIdsBeforeLoad ?? [],
                SameSourceInstanceIdsAfterLoad = after,
                CandidateInstanceId = candidate,
            };
            await transactions.WriteAsync(updated, evidenceTimeout.Token).ConfigureAwait(false);
            return updated;
        }
        catch (Exception evidenceException)
        {
            LogSafely(() => logger.Warning($"LM Studio apply failure evidence unavailable: {evidenceException.GetType().Name}"));
            return updated;
        }
    }

    /// <summary>确认源模型的 native 实例身份可用（未被隔离）。</summary>
    private void EnsureNativeSourceIdentityUsable(string sourceModelKey)
    {
        if (client.HasUnusableNativeInstances(sourceModelKey))
        {
            throw new InvalidDataException("目标源模型含缺失或重复 native instance id；无法安全确定生命周期状态。");
        }
    }

    /// <summary>判断事务失败/恢复失败阶段是否已进入“恢复原实例”的阶段（可作为已恢复证据）。</summary>
    private static bool HasOriginalRecoveryEvidence(LmStudioTemplateTransactionRecord record) =>
        IsOriginalRecoveryStage(record.FailureStage) || IsOriginalRecoveryStage(record.LastRecoveryFailureStage);

    /// <summary>判断阶段是否属于“恢复原实例”阶段。</summary>
    private static bool IsOriginalRecoveryStage(LmStudioLifecycleStage stage) => stage is
        LmStudioLifecycleStage.LoadOriginal or LmStudioLifecycleStage.ValidateOriginal or LmStudioLifecycleStage.ProbeOriginal or LmStudioLifecycleStage.RecoveryCommit;

    /// <summary>更新事务状态并落盘（维护状态机派生字段：稳定状态、同源实例清单、候选 ID、失败阶段等）。</summary>
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

    /// <summary>更新 schema-v4 事务的持久化阶段（可附带备份工件信息）并落盘。</summary>
    private async Task<LmStudioTemplateTransactionRecord> UpdatePersistenceRecordAsync(LmStudioTemplateTransactionRecord record, LmStudioPersistenceStage stage, string detail, CancellationToken cancellationToken, LmStudioDefaultsBackupArtifact? backup = null)
    {
        if (record.SchemaVersion < 4)
        {
            throw new InvalidOperationException("只有 schema-v4 事务可以记录持久 defaults 阶段。");
        }

        LmStudioTemplateTransactionRecord updated = record with
        {
            UpdatedAt = DateTimeOffset.Now,
            Detail = detail,
            PersistenceStage = stage,
            EncryptedDefaultsBackupPath = backup?.Path ?? record.EncryptedDefaultsBackupPath,
            DefaultsBackupPlaintextSha256 = backup?.PlaintextSha256 ?? record.DefaultsBackupPlaintextSha256,
        };
        await transactions.WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>获取生命周期文件锁（独占句柄）；同一事务可重入，不同事务或他进程持锁即抛错。</summary>
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

    /// <summary>释放生命周期锁（校验归属一致）。</summary>
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

    /// <summary>汇报阶段进度：记日志并广播事件；订阅方异常绝不影响事务（仅观察性输出）。</summary>
    private void ReportProgress(string stage)
    {
        LogSafely(() => logger.Info("LM Studio lifecycle stage: " + stage));
        try
        {
            ProgressChanged?.Invoke(this, stage);
        }
        catch (Exception exception)
        {
            // UI 进度只是观察性输出：正在关闭/已释放的 WinForms 订阅者
            // 绝不能把一个已验证的生命周期步骤变成回滚。
            LogSafely(() => logger.Warning($"LM Studio lifecycle progress subscriber unavailable: {exception.GetType().Name}"));
        }
    }

    /// <summary>安全执行日志写入：日志与进度仅是观察性输出，绝不作为事务前置条件。</summary>
    private static void LogSafely(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>取哈希前 12 个字符的短展示形式。</summary>
    private static string ShortHash(string value) => value[..Math.Min(12, value.Length)];
}

/// <summary>模板修复失败异常：携带原始异常、自动回滚结果、修复计划与失败阶段。</summary>
public sealed class LmStudioTemplateApplyException : InvalidOperationException
{
    /// <summary>以完整上下文构造异常。</summary>
    public LmStudioTemplateApplyException(string message, Exception innerException, LmStudioRollbackResult rollback, LmStudioTemplateRepairPlan plan, LmStudioLifecycleStage failureStage)
        : base(message, innerException)
    {
        Rollback = rollback;
        Plan = plan;
        FailureStage = failureStage;
    }

    /// <summary>自动回滚的执行结果。</summary>
    public LmStudioRollbackResult Rollback { get; }

    /// <summary>触发失败的修复计划。</summary>
    public LmStudioTemplateRepairPlan Plan { get; }

    /// <summary>失败发生时的生命周期阶段。</summary>
    public LmStudioLifecycleStage FailureStage { get; }
}
