using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed class LmStudioTemplateRepairPlanner(
    ILmStudioInstanceController instanceController,
    IGgufChatTemplateReader ggufReader,
    IPromptTemplateRepairService templateRepair,
    LmStudioTemplateTransactionStore transactions)
{
    public async Task<LmStudioTemplateRepairPlan> CreatePlanAsync(
        ModelProfile selectedModel,
        CodexInstructionHierarchyProbeResult originalProbe,
        CancellationToken cancellationToken = default)
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
        LmStudioModelFileResolution? resolution = await LmStudioModelFileLocator.TryResolveDetailedAsync(authoritative, cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            throw new FileNotFoundException("无法从 lms ls --json --variants 唯一定位当前 loaded instance 对应的 GGUF；自动修复已阻断，请使用只读手工选择/导出流程。");
        }

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

        (LmStudioRuntimeTemplateProvenance provenance, string? originalRuntimeTemplate) =
            failureCode == CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder
                ? await ResolveV2ProvenanceAsync(snapshot, analysis, originalProbe, cancellationToken).ConfigureAwait(false)
                : ResolveBuiltInProvenance(originalProbe);

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
            originalRuntimeTemplate);
    }

    private async Task<(LmStudioRuntimeTemplateProvenance Provenance, string Template)> ResolveV2ProvenanceAsync(
        LmStudioLoadedInstanceSnapshot snapshot,
        GgufChatTemplateAnalysis analysis,
        CodexInstructionHierarchyProbeResult originalProbe,
        CancellationToken cancellationToken)
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
                string recreated = templateRepair.RecreateKnownTemplate(
                    analysis,
                    PromptTemplateRepairService.LegacyLeadingRuleVersion,
                    record.PatchedTemplateSha256);
                matches.Add((record, recreated));
            }
            catch (InvalidDataException)
            {
                // A completed record whose deterministic hash no longer matches is
                // not provenance for the currently loaded runtime template.
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

        (LmStudioTemplateTransactionRecord evidence, string template) = matches
            .OrderByDescending(match => match.Record.UpdatedAt)
            .First();
        return (
            new LmStudioRuntimeTemplateProvenance(
                LmStudioRuntimeTemplateMode.ManagerRule,
                PromptTemplateRepairService.LegacyLeadingRuleVersion,
                evidence.PatchedTemplateSha256,
                evidence.TransactionId),
            template);
    }

    private static (LmStudioRuntimeTemplateProvenance Provenance, string? Template) ResolveBuiltInProvenance(
        CodexInstructionHierarchyProbeResult originalProbe)
    {
        if (!originalProbe.Control.Passed || originalProbe.LeadingDeveloper.Passed ||
            originalProbe.FailureCode is not (CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole))
        {
            throw new InvalidDataException("当前失败没有形成受支持的内置 Qwen 模板行为签名；卸载前已阻断。");
        }

        return (new LmStudioRuntimeTemplateProvenance(LmStudioRuntimeTemplateMode.BuiltIn), null);
    }

    private static bool HasExactV2Behavior(CodexInstructionHierarchyProbeResult probe) =>
        probe.Control.Passed &&
        probe.LeadingDeveloper.Passed &&
        probe.ConversationControl.Passed &&
        !probe.ContinuationDeveloper.Passed &&
        string.Equals(probe.FailureCode, CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder, StringComparison.Ordinal);

    private static bool MatchesCompletedV2Evidence(
        LmStudioTemplateTransactionRecord record,
        LmStudioLoadedInstanceSnapshot snapshot,
        GgufChatTemplateAnalysis analysis) =>
        record.State == LmStudioTemplateTransactionState.Completed &&
        record.RuleVersion.Equals(PromptTemplateRepairService.LegacyLeadingRuleVersion, StringComparison.Ordinal) &&
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

    private static bool CompatibleExact(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) ||
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
