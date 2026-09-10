using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// LM Studio 模板修复计划器：根据原始探测失败码与实例快照，定位 GGUF、读取模板、
/// 生成修复预览，并推导运行时模板来源（内置模板或旧 v2 规则）与可选的每模型默认值持久化计划。
/// 全程保守：任何身份或指纹不一致都会在卸载实例前阻断。
/// </summary>
public sealed class LmStudioTemplateRepairPlanner(
    ILmStudioInstanceController instanceController,
    IGgufChatTemplateReader ggufReader,
    IPromptTemplateRepairService templateRepair,
    LmStudioTemplateTransactionStore transactions,
    ILmStudioModelFileLocator? modelFileLocator = null,
    LmStudioPerModelDefaultsStore? perModelDefaultsStore = null,
    Func<string?>? lmStudioVersionProvider = null)
{
    private readonly ILmStudioModelFileLocator modelFileLocator = modelFileLocator ?? new LmStudioModelFileLocator();
    private readonly Func<string?> lmStudioVersionProvider = lmStudioVersionProvider ?? LmStudioLocalVersionDetector.Detect;

    /// <summary>为选中的已加载 LLM 实例创建模板修复计划（详见类说明）。</summary>
    public async Task<LmStudioTemplateRepairPlan> CreatePlanAsync(ModelProfile selectedModel, CodexInstructionHierarchyProbeResult originalProbe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(originalProbe);
        string failureCode = originalProbe.FailureCode ?? throw new InvalidOperationException("LM Studio 模板修复缺少稳定失败码。");
        if (failureCode is not (CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or
            CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or
            CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder))
        {
            throw new InvalidOperationException($"失败码 {failureCode} 不允许自动修改 LM Studio Prompt Template。");
        }

        if (selectedModel.IsLoaded != true || selectedModel.ModelType is not null && !selectedModel.ModelType.Equals("llm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只有当前已加载的 LLM instance 才能创建运行时模板修复计划。");
        }

        // 捕获实例快照并以其为权威，重写选中模型的身份与加载信息
        LmStudioLoadedInstanceSnapshot snapshot = await instanceController.CaptureAsync(selectedModel.Id, cancellationToken).ConfigureAwait(false);
        ModelProfile authoritative = selectedModel with
        {
            Id = snapshot.InstanceId,
            LoadedInstanceId = snapshot.InstanceId,
            SourceModelKey = snapshot.SourceModelKey,
            SelectedVariant = snapshot.SelectedVariant,
            Architecture = snapshot.Architecture,
            Quantization = snapshot.Quantization,
            Parameters = snapshot.Parameters,
            ModelType = snapshot.ModelType,
            MaxContextLength = snapshot.MaxContextLength,
            LoadedContextLength = snapshot.LoadConfiguration.ContextLength,
            LoadedConfiguration = snapshot.LoadConfiguration,
            RemainingTtlSeconds = snapshot.RemainingTtlSeconds,
            AvailableVariants = snapshot.LoadTarget?.AvailableVariants,
            Format = snapshot.LoadTarget?.Format,
        };
        LmStudioModelFileResolutionAttempt resolutionAttempt = await modelFileLocator.ResolveAsync(authoritative, snapshot.Endpoint, cancellationToken).ConfigureAwait(false);
        if (!resolutionAttempt.Succeeded || resolutionAttempt.Resolution is null)
        {
            throw new FileNotFoundException($"无法唯一定位当前 loaded instance 对应的 GGUF；自动修复已阻断。{resolutionAttempt.Diagnostic} 请使用只读手工选择/导出流程。");
        }

        LmStudioModelFileResolution resolution = resolutionAttempt.Resolution;

        // lms CLI 的定位结果必须与 native 实例身份逐项一致，否则拒绝自动修复
        if (!string.Equals(resolution.SourceModelKey, snapshot.SourceModelKey, StringComparison.OrdinalIgnoreCase) ||
            !CompatibleExact(snapshot.SelectedVariant, resolution.SelectedVariant) ||
            !CompatibleExact(snapshot.Quantization, resolution.Quantization) ||
            !CompatibleExact(snapshot.Architecture, resolution.Architecture))
        {
            throw new InvalidDataException("lms CLI 解析到的 GGUF 变体、量化或架构与 native loaded instance 不一致；拒绝自动修复。");
        }

        GgufChatTemplateAnalysis analysis = await ggufReader.ReadAsync(resolution.FilePath, cancellationToken).ConfigureAwait(false);
        if (!CompatibleExact(snapshot.Architecture, analysis.Architecture))
        {
            throw new InvalidDataException($"GGUF architecture={analysis.Architecture ?? "unknown"}，但 loaded instance 报告 {snapshot.Architecture ?? "unknown"}；拒绝修补可能错误的文件。");
        }

        PromptTemplateRepairPreview preview = templateRepair.CreatePreview(analysis);
        if (preview.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired) ||
            string.IsNullOrWhiteSpace(preview.PatchedTemplate) ||
            string.IsNullOrWhiteSpace(preview.PatchedTemplateSha256))
        {
            throw new InvalidDataException($"当前 GGUF Prompt Template 不满足保守修补规则：{preview.Detail}");
        }

        // 续轮指令顺序失败 → 运行时是旧 v2 规则模板，需要联合证据回溯；否则视为内置模板
        (LmStudioRuntimeTemplateProvenance provenance, string? originalRuntimeTemplate) =
            failureCode == CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder
                ? await ResolveV2ProvenanceAsync(snapshot, analysis, originalProbe, cancellationToken).ConfigureAwait(false)
                : ResolveBuiltInProvenance(originalProbe);

        string? lmStudioVersion = null;
        LmStudioPerModelDefaultsPlan? persistentDefaults = null;
        if (perModelDefaultsStore is not null)
        {
            lmStudioVersion = lmStudioVersionProvider();
            persistentDefaults = await perModelDefaultsStore.CreatePlanAsync(snapshot.Endpoint, lmStudioVersion, resolution, analysis, preview, provenance, cancellationToken).ConfigureAwait(false);
        }

        return new LmStudioTemplateRepairPlan(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            failureCode,
            snapshot,
            resolution,
            analysis,
            preview,
            provenance,
            originalProbe,
            originalRuntimeTemplate,
            persistentDefaults,
            lmStudioVersion);
    }

    /// <summary>
    /// 回溯旧 v2 规则模板的来源：要求失败行为与 v2 精确签名一致，且存在一条
    /// “实例/config/GGUF 指纹 + v2 模板哈希可复算”的 completed 事务佐证；
    /// 多条佐证哈希冲突时拒绝猜测。
    /// </summary>
    private async Task<(LmStudioRuntimeTemplateProvenance Provenance, string Template)> ResolveV2ProvenanceAsync(LmStudioLoadedInstanceSnapshot snapshot, GgufChatTemplateAnalysis analysis, CodexInstructionHierarchyProbeResult originalProbe, CancellationToken cancellationToken)
    {
        if (!HasExactV2Behavior(originalProbe))
        {
            throw new InvalidDataException("后置 developer 失败没有形成 v2 的精确四阶段行为签名；卸载前已阻断。");
        }

        IReadOnlyList<LmStudioTemplateTransactionRecord> completed = await transactions.ListCompletedAsync(cancellationToken).ConfigureAwait(false);
        List<(LmStudioTemplateTransactionRecord Record, string Template)> matches = [];
        foreach (LmStudioTemplateTransactionRecord record in completed)
        {
            if (!MatchesCompletedV2Evidence(record, snapshot, analysis))
            {
                continue;
            }

            try
            {
                string recreated = templateRepair.RecreateKnownTemplate(analysis, PromptTemplateRepairService.LegacyLeadingRuleVersion, record.PatchedTemplateSha256);
                matches.Add((record, recreated));
            }
            catch (InvalidDataException)
            {
                // completed 记录的确定性哈希已无法复算时，它不能作为当前运行时模板的来源佐证
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidDataException("当前行为像旧 v2 模板，但没有 completed 事务、实例/config、GGUF 指纹和 v2 SHA 的联合证据；自动升级已在 unload 前阻断。请手工导出 v3，或先以内置模板重新加载后再修复。");
        }

        string[] hashes = matches.Select(match => match.Record.PatchedTemplateSha256).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (hashes.Length != 1)
        {
            throw new InvalidDataException("多个 completed 事务对当前 v2 模板哈希给出冲突结论；拒绝猜测回滚来源。");
        }

        (LmStudioTemplateTransactionRecord evidence, string template) = matches.OrderByDescending(match => match.Record.UpdatedAt).First();
        return (new LmStudioRuntimeTemplateProvenance(LmStudioRuntimeTemplateMode.ManagerRule, PromptTemplateRepairService.LegacyLeadingRuleVersion, evidence.PatchedTemplateSha256, evidence.TransactionId), template);
    }

    /// <summary>推导内置模板来源：失败行为必须精确匹配“首条指令失败”或“仅前缀续轮失败”两种内置模板签名之一。</summary>
    private static (LmStudioRuntimeTemplateProvenance Provenance, string? Template) ResolveBuiltInProvenance(CodexInstructionHierarchyProbeResult originalProbe)
    {
        bool leadingInstructionFailure =
            originalProbe.Control.Passed &&
            !originalProbe.LeadingDeveloper.Passed &&
            originalProbe.LeadingDeveloper.HttpStatus is not null &&
            !originalProbe.ConversationControl.Passed &&
            originalProbe.ConversationControl.HttpStatus is null &&
            !originalProbe.ContinuationDeveloper.Passed &&
            originalProbe.ContinuationDeveloper.HttpStatus is null &&
            originalProbe.FailureCode is CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole;
        bool prefixOnlyContinuationFailure =
            originalProbe.Control.Passed &&
            originalProbe.LeadingDeveloper.Passed &&
            originalProbe.ConversationControl.Passed &&
            !originalProbe.ContinuationDeveloper.Passed &&
            originalProbe.ContinuationDeveloper.HttpStatus is not null &&
            string.Equals(originalProbe.FailureCode, CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder, StringComparison.Ordinal);
        if (!leadingInstructionFailure && !prefixOnlyContinuationFailure)
        {
            throw new InvalidDataException("当前失败没有形成受支持的内置 Qwen 模板精确四阶段行为签名；卸载前已阻断。");
        }

        return (new LmStudioRuntimeTemplateProvenance(LmStudioRuntimeTemplateMode.BuiltIn), null);
    }

    /// <summary>判断探测结果是否为 v2 规则模板的精确行为签名（前三步通过、续轮失败且失败码匹配）。</summary>
    private static bool HasExactV2Behavior(CodexInstructionHierarchyProbeResult probe) =>
        probe.Control.Passed &&
        probe.LeadingDeveloper.Passed &&
        probe.ConversationControl.Passed &&
        !probe.ContinuationDeveloper.Passed &&
        string.Equals(probe.FailureCode, CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder, StringComparison.Ordinal);

    /// <summary>判断 completed 事务是否与当前实例/GGUF 全量一致（状态、规则版本、实例身份、加载配置与 GGUF 五重指纹）。</summary>
    private static bool MatchesCompletedV2Evidence(LmStudioTemplateTransactionRecord record, LmStudioLoadedInstanceSnapshot snapshot, GgufChatTemplateAnalysis analysis) =>
        record.State == LmStudioTemplateTransactionState.Completed &&
        string.Equals(record.RuleVersion, PromptTemplateRepairService.LegacyLeadingRuleVersion, StringComparison.Ordinal) &&
        record.PatchedInstanceId?.Equals(snapshot.InstanceId, StringComparison.Ordinal) == true &&
        record.OriginalInstance.Endpoint.AbsoluteUri.TrimEnd('/').Equals(snapshot.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
        record.OriginalInstance.SourceModelKey.Equals(snapshot.SourceModelKey, StringComparison.OrdinalIgnoreCase) &&
        CompatibleExact(record.OriginalInstance.SelectedVariant, snapshot.SelectedVariant) &&
        CompatibleExact(record.OriginalInstance.Architecture, snapshot.Architecture) &&
        CompatibleExact(record.OriginalInstance.Quantization, snapshot.Quantization) &&
        CompatibleExact(record.OriginalInstance.Parameters, snapshot.Parameters) &&
        record.OriginalInstance.MaxContextLength == snapshot.MaxContextLength &&
        LmStudioClient.LoadConfigurationsEqual(record.OriginalInstance.LoadConfiguration, snapshot.LoadConfiguration) &&
        Path.GetFullPath(record.GgufFilePath).Equals(Path.GetFullPath(analysis.FilePath), StringComparison.OrdinalIgnoreCase) &&
        record.GgufFileName.Equals(analysis.FileName, StringComparison.OrdinalIgnoreCase) &&
        record.GgufLength == analysis.FileLength &&
        record.GgufLastWriteTimeUtc == analysis.LastWriteTimeUtc &&
        record.GgufVersion == analysis.GgufVersion &&
        record.OriginalTemplateSha256.Equals(analysis.TemplateSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>忽略大小写且“同空同有”的精确匹配（一侧空白即要求另一侧也空白）。</summary>
    private static bool CompatibleExact(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) ||
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
