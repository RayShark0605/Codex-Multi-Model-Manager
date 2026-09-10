using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

/// <summary>
/// LM Studio「每模型默认值」存储：以 DPAPI 加密备份 + 字段级精确改写的方式，
/// 把管理器 v3 Prompt Template 持久化到 LM Studio 的 user-concrete-model-default-config 目录。
/// 全程施加安全边界：仅 loopback、仅受验证的 LM Studio 版本、标识与路径防穿越、
/// 禁止 reparse point、候选只允许改 promptTemplate 一个字段。
/// </summary>
public sealed class LmStudioPerModelDefaultsStore
{
    /// <summary>持久化目标字段：llm.load.promptTemplate。</summary>
    public const string PromptTemplateKey = "llm.load.promptTemplate";
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumJsonDepth = 32;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly IPromptTemplateRepairService templateRepair;
    private readonly IAtomicBatchWriter atomicWriter;
    private readonly ILmStudioDefaultsProtector protector;
    private readonly string rootDirectory;

    /// <summary>默认构造：Windows DPAPI 保护器与 LM Studio 默认根目录。</summary>
    public LmStudioPerModelDefaultsStore(IPromptTemplateRepairService templateRepair, IAtomicBatchWriter atomicWriter)
        : this(templateRepair, atomicWriter, new WindowsCurrentUserDpapiProtector(), ResolveDefaultRoot())
    {
    }

    /// <summary>测试用构造：可注入保护器与根目录。</summary>
    internal LmStudioPerModelDefaultsStore(IPromptTemplateRepairService templateRepair, IAtomicBatchWriter atomicWriter, ILmStudioDefaultsProtector protector, string rootDirectory)
    {
        this.templateRepair = templateRepair ?? throw new ArgumentNullException(nameof(templateRepair));
        this.atomicWriter = atomicWriter ?? throw new ArgumentNullException(nameof(atomicWriter));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>持久化根目录。</summary>
    public string RootDirectory => rootDirectory;

    /// <summary>按 concrete model identifier 推导 defaults 文件全路径（带防穿越与 reparse point 校验）。</summary>
    public string GetDefaultsPath(string concreteModelIdentifier)
    {
        string[] segments = ValidateConcreteIdentifier(concreteModelIdentifier);
        string path = rootDirectory;
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        path += ".json";
        string fullPath = Path.GetFullPath(path);
        if (!IsUnderRoot(fullPath, rootDirectory))
        {
            throw new InvalidDataException("concrete model identifier 越出 LM Studio per-model defaults 根目录。");
        }

        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    /// <summary>
    /// 创建持久化计划：校验环境/标识/目标模板可确定性重建，读取稳定的 defaults 快照，
    /// 分类原 promptTemplate 字段（Missing/v2/v3），生成只改该字段的候选并复核。
    /// </summary>
    public async Task<LmStudioPerModelDefaultsPlan> CreatePlanAsync(
        Uri endpoint,
        string? lmStudioVersion,
        LmStudioModelFileResolution resolution,
        GgufChatTemplateAnalysis analysis,
        PromptTemplateRepairPreview targetPreview,
        LmStudioRuntimeTemplateProvenance runtimeProvenance,
        CancellationToken cancellationToken = default)
    {
        ValidateSupportedEnvironment(endpoint, lmStudioVersion);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(targetPreview);
        ArgumentNullException.ThrowIfNull(runtimeProvenance);
        if (string.IsNullOrWhiteSpace(resolution.ConcreteModelIdentifier) ||
            !resolution.Source.StartsWith("lms ps", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("当前 GGUF 没有经 lms ps 严格验证的 concrete model identifier；持久化已阻断。");
        }

        string concreteIdentifier = NormalizeConcreteIdentifier(resolution.ConcreteModelIdentifier);
        string[] concreteSegments = ValidateConcreteIdentifier(concreteIdentifier);
        if (!File.Exists(resolution.FilePath) ||
            !Path.GetExtension(resolution.FilePath).Equals(".gguf", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolution.FilePath).Equals(concreteSegments[^1], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("concrete model identifier 与已验证 GGUF 文件不一致；持久化已阻断。");
        }

        if (targetPreview.Status is not (PromptTemplateRepairStatus.Supported or PromptTemplateRepairStatus.UpgradeRequired or PromptTemplateRepairStatus.AlreadyCompatible) ||
            string.IsNullOrWhiteSpace(targetPreview.PatchedTemplate) ||
            string.IsNullOrWhiteSpace(targetPreview.PatchedTemplateSha256) ||
            !targetPreview.RuleVersion.Equals(PromptTemplateRepairService.CurrentRuleVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("目标 Prompt Template 不是经过完整校验的当前 v3 规则；持久化已阻断。");
        }

        string recreatedTarget = templateRepair.RecreateKnownTemplate(analysis, targetPreview.RuleVersion, targetPreview.PatchedTemplateSha256);
        if (!recreatedTarget.Equals(targetPreview.PatchedTemplate, StringComparison.Ordinal))
        {
            throw new InvalidDataException("目标 Prompt Template 正文与可重建的 v3 规则不一致；持久化已阻断。");
        }

        string filePath = GetDefaultsPath(concreteIdentifier);
        StableFileSnapshot snapshot = await ReadStableSnapshotAsync(filePath, cancellationToken).ConfigureAwait(false);
        JsonObject originalRoot = ParseAndValidateRoot(snapshot.Bytes);
        PromptField originalField = ReadPromptField(originalRoot);
        (LmStudioPersistentTemplateFieldState fieldState, string? originalRule, string? originalTemplateSha) =
            ClassifyOriginalField(originalField, analysis, targetPreview, runtimeProvenance);

        LmStudioPerModelDefaultsMutation mutation = fieldState switch
        {
            LmStudioPersistentTemplateFieldState.Missing => LmStudioPerModelDefaultsMutation.Add,
            LmStudioPersistentTemplateFieldState.ManagerV2 => LmStudioPerModelDefaultsMutation.Upgrade,
            LmStudioPersistentTemplateFieldState.ManagerV3 => LmStudioPerModelDefaultsMutation.NoOp,
            _ => throw new InvalidDataException("不支持的原 Prompt Template 字段状态。"),
        };

        byte[] candidateBytes;
        if (mutation == LmStudioPerModelDefaultsMutation.NoOp)
        {
            candidateBytes = snapshot.Bytes.ToArray();
        }
        else
        {
            JsonObject candidateRoot = (JsonObject)originalRoot.DeepClone();
            ReplacePromptField(candidateRoot, CreateTargetPromptField(targetPreview.PatchedTemplate));
            EnsureOnlyPromptFieldChanged(originalRoot, candidateRoot);
            candidateBytes = Serialize(candidateRoot);
            ValidateTargetCandidate(candidateBytes, targetPreview.PatchedTemplateSha256);
        }

        FileFingerprint candidateFingerprint = FingerprintCandidate(candidateBytes);
        return new LmStudioPerModelDefaultsPlan(
            concreteIdentifier,
            filePath,
            lmStudioVersion!,
            snapshot.Fingerprint,
            candidateFingerprint,
            fieldState,
            originalRule,
            originalTemplateSha,
            targetPreview.RuleVersion,
            targetPreview.PatchedTemplateSha256,
            mutation,
            snapshot.Bytes,
            candidateBytes);
    }

    /// <summary>
    /// 创建并核验加密备份：确认 defaults 未变后，把原始字节 DPAPI 加密落盘，
    /// 再解密读回复核哈希与字节；任何失败都会删除半成品备份文件。
    /// </summary>
    public async Task<LmStudioDefaultsBackupArtifact> CreateVerifiedBackupAsync(LmStudioPerModelDefaultsPlan plan, string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        string fullBackupPath = Path.GetFullPath(backupPath);
        if (File.Exists(fullBackupPath))
        {
            throw new IOException("LM Studio defaults 加密备份已存在；拒绝覆盖恢复证据。");
        }

        FileFingerprint current = await FileFingerprintService.CaptureAsync(plan.FilePath, cancellationToken).ConfigureAwait(false);
        if (!FileFingerprintService.Matches(plan.OriginalFingerprint, current))
        {
            throw new IOException("LM Studio per-model defaults 在 Preview 后发生变化；备份和写入均已阻断。");
        }

        byte[] encrypted = protector.Protect(plan.OriginalBytes);
        string? directory = Path.GetDirectoryName(fullBackupPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidDataException("LM Studio defaults 备份路径无效。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = fullBackupPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullBackupPath);
            byte[] encryptedReadback = await File.ReadAllBytesAsync(fullBackupPath, cancellationToken).ConfigureAwait(false);
            byte[] decrypted = protector.Unprotect(encryptedReadback);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(decrypted), SHA256.HashData(plan.OriginalBytes)) ||
                !decrypted.AsSpan().SequenceEqual(plan.OriginalBytes))
            {
                throw new CryptographicException("LM Studio defaults DPAPI 备份解密校验失败。");
            }

            return new LmStudioDefaultsBackupArtifact(
                fullBackupPath,
                plan.OriginalFingerprint.Sha256,
                Convert.ToHexString(SHA256.HashData(encryptedReadback)));
        }
        catch
        {
            if (File.Exists(fullBackupPath))
            {
                File.Delete(fullBackupPath);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>应用持久化计划：NoOp 只复核；否则以原子批量写入候选（附提交后校验）并复核落地结果。</summary>
    public async Task ApplyAsync(LmStudioPerModelDefaultsPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureNoReparsePoints(plan.FilePath);
        if (plan.Mutation == LmStudioPerModelDefaultsMutation.NoOp)
        {
            await VerifyAppliedAsync(plan, cancellationToken).ConfigureAwait(false);
            return;
        }

        var mutation = new ConfigMutation(
            "load.fields[llm.load.promptTemplate]",
            plan.Mutation == LmStudioPerModelDefaultsMutation.Add ? ConfigMutationKind.Add : ConfigMutationKind.Change,
            plan.Mutation == LmStudioPerModelDefaultsMutation.Add ? null : "manager-v2",
            "manager-v3");
        var change = new PlannedFileChange(
            plan.FilePath,
            plan.OriginalFingerprint,
            plan.CandidateBytes,
            [mutation],
            bytes =>
            {
                ValidateTargetCandidate(bytes, plan.TargetTemplateSha256);
                return ValueTask.CompletedTask;
            });
        await atomicWriter.WriteAsync([change], cancellationToken).ConfigureAwait(false);
        await VerifyAppliedAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>复核已应用的计划：稳定读取当前文件，校验目标模板哈希与候选 SHA 一致。</summary>
    public static async Task<FileFingerprint> VerifyAppliedAsync(LmStudioPerModelDefaultsPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureNoReparsePoints(plan.FilePath);
        StableFileSnapshot snapshot = await ReadStableSnapshotAsync(plan.FilePath, cancellationToken).ConfigureAwait(false);
        ValidateTargetCandidate(snapshot.Bytes, plan.TargetTemplateSha256);
        if (!snapshot.Fingerprint.Sha256.Equals(plan.CandidateFingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("LM Studio per-model defaults 写入后的文件 SHA 与候选文件不一致。");
        }

        return snapshot.Fingerprint;
    }

    /// <summary>
    /// 从加密备份恢复：核验备份（哈希 + 解密 + 与原始字节一致）；
    /// 当前已是原始状态则跳过；否则优先整文件精确恢复，不能精确恢复时执行
    /// 字段级恢复（仅当 promptTemplate 仍归管理器所有，保留其他字段的并发变化）。
    /// </summary>
    public async Task<LmStudioDefaultsRestoreResult> RestoreAsync(LmStudioPerModelDefaultsPlan plan, LmStudioDefaultsBackupArtifact backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(backup);
        byte[] originalBytes;
        try
        {
            byte[] encrypted = await File.ReadAllBytesAsync(backup.Path, cancellationToken).ConfigureAwait(false);
            if (!Convert.ToHexString(SHA256.HashData(encrypted)).Equals(backup.EncryptedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("加密备份 SHA 不一致。");
            }

            originalBytes = protector.Unprotect(encrypted);
            if (!Convert.ToHexString(SHA256.HashData(originalBytes)).Equals(backup.PlaintextSha256, StringComparison.OrdinalIgnoreCase) ||
                !originalBytes.AsSpan().SequenceEqual(plan.OriginalBytes))
            {
                throw new CryptographicException("加密备份明文 SHA 或原始字节不一致。");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception)
        {
            return new LmStudioDefaultsRestoreResult(false, true, $"LM Studio defaults 加密备份无法验证：{exception.Message}");
        }

        StableFileSnapshot current;
        try
        {
            current = await ReadStableSnapshotAsync(plan.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new LmStudioDefaultsRestoreResult(false, true, $"无法安全读取当前 LM Studio defaults：{exception.Message}");
        }

        if (current.Fingerprint.Sha256.Equals(plan.OriginalFingerprint.Sha256, StringComparison.OrdinalIgnoreCase) &&
            current.Bytes.AsSpan().SequenceEqual(originalBytes))
        {
            return new LmStudioDefaultsRestoreResult(true, false, "LM Studio defaults 已是事务前原始字节；无需重复恢复。", current.Fingerprint);
        }

        byte[] restoreBytes;
        bool exactRestore = current.Fingerprint.Sha256.Equals(plan.CandidateFingerprint.Sha256, StringComparison.OrdinalIgnoreCase) &&
            current.Bytes.AsSpan().SequenceEqual(plan.CandidateBytes);
        if (exactRestore)
        {
            restoreBytes = originalBytes;
        }
        else
        {
            try
            {
                JsonObject currentRoot = ParseAndValidateRoot(current.Bytes);
                PromptField currentPrompt = ReadPromptField(currentRoot);
                JsonObject originalRoot = ParseAndValidateRoot(originalBytes);
                PromptField originalPrompt = ReadPromptField(originalRoot);
                if (PromptFieldsEqual(currentPrompt, originalPrompt))
                {
                    return new LmStudioDefaultsRestoreResult(true, false, "管理器拥有的 Prompt Template 字段已经处于事务前状态；保留其他并发字段变化。", current.Fingerprint);
                }

                // 字段被外部改成未知内容时绝不覆盖用户配置
                if (currentPrompt.Template is null ||
                    !ComputeTemplateSha(currentPrompt.Template).Equals(plan.TargetTemplateSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new LmStudioDefaultsRestoreResult(false, true, "Prompt Template 字段已被外部修改为未知内容；为避免覆盖用户配置，恢复已阻断。");
                }

                RestorePromptField(currentRoot, originalPrompt);
                restoreBytes = Serialize(currentRoot);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                return new LmStudioDefaultsRestoreResult(false, true, $"Prompt Template 字段无法安全执行字段级恢复：{exception.Message}");
            }
        }

        try
        {
            var change = new PlannedFileChange(
                plan.FilePath,
                current.Fingerprint,
                restoreBytes,
                [new ConfigMutation("load.fields[llm.load.promptTemplate]", ConfigMutationKind.Restore, "manager-v3", plan.OriginalFieldState.ToString())],
                bytes =>
                {
                    ParseAndValidateRoot(bytes);
                    return ValueTask.CompletedTask;
                });
            await atomicWriter.WriteAsync([change], cancellationToken).ConfigureAwait(false);
            FileFingerprint restored = await FileFingerprintService.CaptureAsync(plan.FilePath, cancellationToken).ConfigureAwait(false);
            return new LmStudioDefaultsRestoreResult(true, false, exactRestore ? "已从 DPAPI 备份精确恢复 LM Studio defaults 原始字节。" : "已只恢复管理器拥有的 Prompt Template 字段，并保留并发产生的其他字段变化。", restored);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new LmStudioDefaultsRestoreResult(false, true, $"LM Studio defaults 恢复写入失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 从 schema-v4 事务记录恢复持久化 defaults：核验记录证据齐全、路径与 concrete identity 一致、
    /// DPAPI 备份可用且哈希匹配，再确定性重建候选并转入 <see cref="RestoreAsync"/>。
    /// </summary>
    public async Task<LmStudioDefaultsRestoreResult> RestoreFromTransactionAsync(LmStudioTemplateTransactionRecord record, GgufChatTemplateAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(analysis);
        if (record.SchemaVersion < 4 ||
            string.IsNullOrWhiteSpace(record.ConcreteModelIdentifier) ||
            string.IsNullOrWhiteSpace(record.PerModelDefaultsPath) ||
            record.OriginalDefaultsFingerprint is null ||
            record.OriginalPersistentTemplateState is null ||
            string.IsNullOrWhiteSpace(record.TargetPersistentRuleVersion) ||
            string.IsNullOrWhiteSpace(record.TargetPersistentTemplateSha256) ||
            string.IsNullOrWhiteSpace(record.CandidateDefaultsSha256) ||
            string.IsNullOrWhiteSpace(record.EncryptedDefaultsBackupPath) ||
            string.IsNullOrWhiteSpace(record.DefaultsBackupPlaintextSha256) ||
            !LmStudioPerModelDefaultsCompatibility.IsSupportedVersion(record.LmStudioVersion))
        {
            return new LmStudioDefaultsRestoreResult(false, true, "schema-v4 事务缺少受支持的 LM Studio 版本或持久 defaults 恢复证据。");
        }

        string expectedPath;
        try
        {
            expectedPath = GetDefaultsPath(record.ConcreteModelIdentifier);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new LmStudioDefaultsRestoreResult(false, true, $"schema-v4 concrete defaults 路径不安全：{exception.Message}");
        }

        if (!Path.GetFullPath(record.PerModelDefaultsPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return new LmStudioDefaultsRestoreResult(false, true, "schema-v4 事务 defaults 路径与 concrete identity 不一致。");
        }

        byte[] encrypted;
        byte[] originalBytes;
        try
        {
            encrypted = await File.ReadAllBytesAsync(record.EncryptedDefaultsBackupPath, cancellationToken).ConfigureAwait(false);
            originalBytes = protector.Unprotect(encrypted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException or Win32Exception)
        {
            return new LmStudioDefaultsRestoreResult(false, true, $"schema-v4 DPAPI 备份不可用：{exception.Message}");
        }

        if (!Convert.ToHexString(SHA256.HashData(originalBytes)).Equals(record.DefaultsBackupPlaintextSha256, StringComparison.OrdinalIgnoreCase) ||
            !Convert.ToHexString(SHA256.HashData(originalBytes)).Equals(record.OriginalDefaultsFingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new LmStudioDefaultsRestoreResult(false, true, "schema-v4 DPAPI 备份明文 SHA 与 journal 不一致。");
        }

        byte[] candidateBytes;
        try
        {
            JsonObject originalRoot = ParseAndValidateRoot(originalBytes);
            string targetTemplate = templateRepair.RecreateKnownTemplate(analysis, record.TargetPersistentRuleVersion, record.TargetPersistentTemplateSha256);
            if (record.OriginalPersistentTemplateState == LmStudioPersistentTemplateFieldState.ManagerV3)
            {
                candidateBytes = originalBytes.ToArray();
            }
            else
            {
                JsonObject candidateRoot = (JsonObject)originalRoot.DeepClone();
                ReplacePromptField(candidateRoot, CreateTargetPromptField(targetTemplate));
                EnsureOnlyPromptFieldChanged(originalRoot, candidateRoot);
                candidateBytes = Serialize(candidateRoot);
            }

            if (!Convert.ToHexString(SHA256.HashData(candidateBytes)).Equals(record.CandidateDefaultsSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new LmStudioDefaultsRestoreResult(false, true, "从加密备份确定性重建的候选 defaults SHA 与 journal 不一致。");
            }
        }
        catch (InvalidDataException exception)
        {
            return new LmStudioDefaultsRestoreResult(false, true, $"无法确定性重建 schema-v4 defaults：{exception.Message}");
        }

        var plan = new LmStudioPerModelDefaultsPlan(
            record.ConcreteModelIdentifier,
            expectedPath,
            record.LmStudioVersion!,
            record.OriginalDefaultsFingerprint,
            FingerprintCandidate(candidateBytes),
            record.OriginalPersistentTemplateState.Value,
            record.OriginalPersistentRuleVersion,
            record.OriginalPersistentTemplateSha256,
            record.TargetPersistentRuleVersion,
            record.TargetPersistentTemplateSha256,
            record.OriginalPersistentTemplateState == LmStudioPersistentTemplateFieldState.Missing
                ? LmStudioPerModelDefaultsMutation.Add
                : record.OriginalPersistentTemplateState == LmStudioPersistentTemplateFieldState.ManagerV2
                    ? LmStudioPerModelDefaultsMutation.Upgrade
                    : LmStudioPerModelDefaultsMutation.NoOp,
            originalBytes,
            candidateBytes);
        var backup = new LmStudioDefaultsBackupArtifact(
            record.EncryptedDefaultsBackupPath,
            record.DefaultsBackupPlaintextSha256,
            Convert.ToHexString(SHA256.HashData(encrypted)));
        return await RestoreAsync(plan, backup, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>核验事务目标：稳定读取当前 defaults，确认目标模板哈希与候选 SHA 均与记录一致。</summary>
    public static async Task<FileFingerprint> VerifyTransactionTargetAsync(LmStudioTemplateTransactionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SchemaVersion < 4 || string.IsNullOrWhiteSpace(record.PerModelDefaultsPath) ||
            string.IsNullOrWhiteSpace(record.TargetPersistentTemplateSha256) || string.IsNullOrWhiteSpace(record.CandidateDefaultsSha256))
        {
            throw new InvalidDataException("事务没有完整的 schema-v4 持久 defaults 证据。");
        }

        StableFileSnapshot snapshot = await ReadStableSnapshotAsync(record.PerModelDefaultsPath, cancellationToken).ConfigureAwait(false);
        ValidateTargetCandidate(snapshot.Bytes, record.TargetPersistentTemplateSha256);
        if (!snapshot.Fingerprint.Sha256.Equals(record.CandidateDefaultsSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("当前 per-model defaults SHA 与 schema-v4 候选证据不一致。");
        }

        return snapshot.Fingerprint;
    }

    /// <summary>
    /// 分类原 promptTemplate 字段：缺失 → Missing；与可重建 v3 一致 → ManagerV3；
    /// 与可重建 v2 一致且有 completed 事务佐证 → ManagerV2；其余（用户自定义）一律拒绝覆盖。
    /// </summary>
    private (LmStudioPersistentTemplateFieldState State, string? RuleVersion, string? TemplateSha256) ClassifyOriginalField(PromptField field, GgufChatTemplateAnalysis analysis, PromptTemplateRepairPreview targetPreview, LmStudioRuntimeTemplateProvenance runtimeProvenance)
    {
        if (field.Template is null)
        {
            return (LmStudioPersistentTemplateFieldState.Missing, null, null);
        }

        string templateSha = ComputeTemplateSha(field.Template);
        if (templateSha.Equals(targetPreview.PatchedTemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            string exactV3 = templateRepair.RecreateKnownTemplate(analysis, PromptTemplateRepairService.CurrentRuleVersion, templateSha);
            if (field.Template.Equals(exactV3, StringComparison.Ordinal))
            {
                return (LmStudioPersistentTemplateFieldState.ManagerV3, PromptTemplateRepairService.CurrentRuleVersion, templateSha);
            }
        }

        try
        {
            string exactV2 = templateRepair.RecreateKnownTemplate(analysis, PromptTemplateRepairService.LegacyLeadingRuleVersion, templateSha);
            bool hasCompletedProvenance = runtimeProvenance.Mode == LmStudioRuntimeTemplateMode.ManagerRule &&
                runtimeProvenance.RuleVersion == PromptTemplateRepairService.LegacyLeadingRuleVersion &&
                runtimeProvenance.TemplateSha256?.Equals(templateSha, StringComparison.OrdinalIgnoreCase) == true &&
                runtimeProvenance.EvidenceTransactionId is not null;
            if (field.Template.Equals(exactV2, StringComparison.Ordinal) && hasCompletedProvenance)
            {
                return (LmStudioPersistentTemplateFieldState.ManagerV2, PromptTemplateRepairService.LegacyLeadingRuleVersion, templateSha);
            }
        }
        catch (InvalidDataException)
        {
        }

        throw new InvalidDataException("检测到未知或用户自定义的 llm.load.promptTemplate；自动持久化不会覆盖该字段。");
    }

    /// <summary>解析并校验 defaults 文件：大小/深度限制、根结构（preset 字符串）、operation/load 容器与 promptTemplate 字段形状。</summary>
    private static JsonObject ParseAndValidateRoot(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException($"LM Studio per-model defaults 文件大小必须在 1 到 {MaximumFileBytes:N0} 字节之间。");
        }

        JsonNode? node = JsonNode.Parse(bytes, nodeOptions: null, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumJsonDepth });
        if (node is not JsonObject root || root["preset"] is not JsonValue preset || !preset.TryGetValue(out string? _))
        {
            throw new InvalidDataException("LM Studio per-model defaults 根必须是 object，且 preset 必须是 string。");
        }

        ValidateFieldsContainer(root, "operation");
        ValidateFieldsContainer(root, "load");
        _ = ReadPromptField(root);
        return root;
    }

    /// <summary>校验指定容器（operation/load）的 fields 数组：每个条目必须有非空字符串 key 与 value。</summary>
    private static void ValidateFieldsContainer(JsonObject root, string name)
    {
        if (root[name] is not JsonObject container || container["fields"] is not JsonArray fields)
        {
            throw new InvalidDataException($"LM Studio per-model defaults 的 {name}.fields 必须是 array。");
        }

        foreach (JsonNode? node in fields)
        {
            if (node is not JsonObject field || field["key"] is not JsonValue keyValue || !keyValue.TryGetValue(out string? key) || string.IsNullOrWhiteSpace(key) || !field.ContainsKey("value"))
            {
                throw new InvalidDataException($"LM Studio per-model defaults 的 {name}.fields 包含无效字段条目。");
            }
        }
    }

    /// <summary>读取 promptTemplate 字段：定位（要求唯一）、校验精确 Jinja 结构，返回索引、深拷贝节点与模板文本。</summary>
    private static PromptField ReadPromptField(JsonObject root)
    {
        JsonArray fields = GetLoadFields(root);
        List<(int Index, JsonObject Field)> matches = [];
        for (int index = 0; index < fields.Count; index++)
        {
            if (fields[index] is JsonObject field && TryGetString(field["key"], out string? key) && key.Equals(PromptTemplateKey, StringComparison.Ordinal))
            {
                matches.Add((index, field));
            }
        }

        if (matches.Count > 1)
        {
            throw new InvalidDataException("llm.load.promptTemplate 在 load.fields 中重复出现；自动持久化已阻断。");
        }

        if (matches.Count == 0)
        {
            return new PromptField(-1, null, null);
        }

        JsonObject fieldObject = matches[0].Field;
        if (fieldObject["value"] is not JsonObject value || value.Count != 2 ||
            !TryGetString(value["type"], out string? type) || !type.Equals("jinja", StringComparison.Ordinal) ||
            value["jinjaPromptTemplate"] is not JsonObject jinja || jinja.Count != 1 ||
            !TryGetString(jinja["template"], out string? template))
        {
            throw new InvalidDataException("llm.load.promptTemplate 的 value 不是受支持的精确 Jinja 配置结构。");
        }

        return new PromptField(matches[0].Index, (JsonObject)fieldObject.DeepClone(), template);
    }

    /// <summary>构造目标 promptTemplate 字段节点（jinja 类型 + 模板正文）。</summary>
    private static JsonObject CreateTargetPromptField(string template) => new()
    {
        ["key"] = PromptTemplateKey,
        ["value"] = new JsonObject
        {
            ["type"] = "jinja",
            ["jinjaPromptTemplate"] = new JsonObject { ["template"] = template },
        },
    };

    /// <summary>比较两个字段是否等值（同为缺失，或模板文本与节点结构都一致）。</summary>
    private static bool PromptFieldsEqual(PromptField left, PromptField right) =>
        left.Template is null && right.Template is null ||
        left.Template is not null && right.Template is not null && left.Template.Equals(right.Template, StringComparison.Ordinal) && JsonNode.DeepEquals(left.Field, right.Field);

    /// <summary>替换 promptTemplate 字段：缺失则追加，存在则原位替换。</summary>
    private static void ReplacePromptField(JsonObject root, JsonObject targetField)
    {
        JsonArray fields = GetLoadFields(root);
        PromptField existing = ReadPromptField(root);
        if (existing.Index < 0)
        {
            fields.Add(targetField);
        }
        else
        {
            fields[existing.Index] = targetField;
        }
    }

    /// <summary>把 promptTemplate 字段恢复为原始形态（原始缺失则移除当前字段）。</summary>
    private static void RestorePromptField(JsonObject root, PromptField original)
    {
        JsonArray fields = GetLoadFields(root);
        PromptField current = ReadPromptField(root);
        if (current.Index < 0)
        {
            throw new InvalidDataException("当前 Prompt Template 字段缺失；不能证明该字段仍由管理器拥有。");
        }

        if (original.Field is null)
        {
            fields.RemoveAt(current.Index);
        }
        else
        {
            fields[current.Index] = original.Field.DeepClone();
        }
    }

    /// <summary>取 load.fields 数组（结构已经过校验）。</summary>
    private static JsonArray GetLoadFields(JsonObject root) =>
        (JsonArray)((JsonObject)root["load"]!)["fields"]!;

    /// <summary>确保候选相对原始只有 promptTemplate 字段变化（去掉该字段后整体深度相等）。</summary>
    private static void EnsureOnlyPromptFieldChanged(JsonObject original, JsonObject candidate)
    {
        JsonObject originalWithoutPrompt = (JsonObject)original.DeepClone();
        JsonObject candidateWithoutPrompt = (JsonObject)candidate.DeepClone();
        RemovePromptField(originalWithoutPrompt);
        RemovePromptField(candidateWithoutPrompt);
        if (!JsonNode.DeepEquals(originalWithoutPrompt, candidateWithoutPrompt))
        {
            throw new InvalidDataException("生成候选 defaults 时检测到 Prompt Template 之外的语义变化。");
        }
    }

    /// <summary>移除全部 promptTemplate 字段（倒序遍历避免索引漂移）。</summary>
    private static void RemovePromptField(JsonObject root)
    {
        JsonArray fields = GetLoadFields(root);
        for (int index = fields.Count - 1; index >= 0; index--)
        {
            if (fields[index] is JsonObject field && TryGetString(field["key"], out string? key) && key.Equals(PromptTemplateKey, StringComparison.Ordinal))
            {
                fields.RemoveAt(index);
            }
        }
    }

    /// <summary>校验候选：结构合法且 promptTemplate 的哈希恰为期望的目标模板哈希。</summary>
    private static void ValidateTargetCandidate(byte[] bytes, string expectedTemplateSha256)
    {
        JsonObject root = ParseAndValidateRoot(bytes);
        PromptField prompt = ReadPromptField(root);
        if (prompt.Template is null || !ComputeTemplateSha(prompt.Template).Equals(expectedTemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("候选 defaults 未包含目标 v3 Prompt Template SHA。");
        }
    }

    /// <summary>序列化为缩进 JSON（UTF-8 无 BOM，末尾补系统换行）。</summary>
    private static byte[] Serialize(JsonObject root) => Utf8NoBom.GetBytes(root.ToJsonString(WriteOptions) + Environment.NewLine);

    /// <summary>为候选字节生成指纹（存在 + 长度 + SHA）。</summary>
    private static FileFingerprint FingerprintCandidate(byte[] bytes) =>
        new(true, bytes.LongLength, null, Convert.ToHexString(SHA256.HashData(bytes)));

    /// <summary>计算模板文本的 SHA-256。</summary>
    private static string ComputeTemplateSha(string template) => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(template)));

    /// <summary>稳定读取文件：前后指纹一致且字节哈希吻合，确保读到的是未被并发修改的完整内容。</summary>
    private static async Task<StableFileSnapshot> ReadStableSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        FileFingerprint before = await FileFingerprintService.CaptureAsync(path, cancellationToken).ConfigureAwait(false);
        if (!before.Exists)
        {
            throw new FileNotFoundException("LM Studio per-model defaults 文件不存在；未知默认结构下不会自动新建。", path);
        }

        if (before.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException($"LM Studio per-model defaults 文件大小超出安全范围：{before.Length:N0} 字节。");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        FileFingerprint after = await FileFingerprintService.CaptureAsync(path, cancellationToken).ConfigureAwait(false);
        if (!FileFingerprintService.Matches(before, after) ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(after.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("LM Studio per-model defaults 在读取期间发生变化；请刷新后重试。");
        }

        return new StableFileSnapshot(bytes, after);
    }

    /// <summary>环境校验：端点合法且为 loopback，LM Studio 版本落在受验证版本族内。</summary>
    private static void ValidateSupportedEnvironment(Uri endpoint, string? lmStudioVersion)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        LmStudioEndpointPolicy.Validate(endpoint);
        if (!endpoint.IsLoopback)
        {
            throw new InvalidOperationException("仅本机 loopback LM Studio 允许自动写入 per-model defaults。");
        }

        if (!LmStudioPerModelDefaultsCompatibility.IsSupportedVersion(lmStudioVersion))
        {
            throw new NotSupportedException($"LM Studio {lmStudioVersion ?? "unknown"} 低于 per-model defaults 受支持的最低版本 {LmStudioPerModelDefaultsCompatibility.MinimumSupportedVersion}；更高版本是否兼容由 defaults 文件结构校验决定。");
        }
    }

    /// <summary>校验 concrete model identifier：相对路径、段合法（无 ./.. 与非法字符）、末段以 .gguf 结尾。</summary>
    private static string[] ValidateConcreteIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathFullyQualified(value) || value.StartsWith('/') || value.StartsWith('\\') || value.Contains(':'))
        {
            throw new InvalidDataException("concrete model identifier 不能是绝对路径、UNC 或驱动器路径。");
        }

        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            !segments[^1].EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("concrete model identifier 必须是无路径穿越的 publisher/.../*.gguf 相对标识。");
        }

        return segments;
    }

    /// <summary>归一化 concrete model identifier（反斜杠统一为正斜杠）。</summary>
    private static string NormalizeConcreteIdentifier(string value) => string.Join('/', ValidateConcreteIdentifier(value));

    /// <summary>判断路径是否位于根目录之下（忽略大小写）。</summary>
    private static bool IsUnderRoot(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>确保目标路径的现有祖先不含 reparse point/junction，防止写入被重定向。</summary>
    private static void EnsureNoReparsePoints(string targetPath)
    {
        string fullPath = Path.GetFullPath(targetPath);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidDataException("per-model defaults 路径没有有效根目录。");
        }

        string current = root;
        string relative = fullPath[root.Length..];
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("per-model defaults 路径的现有祖先包含 reparse point/junction；自动写入已阻断。");
            }
        }
    }

    /// <summary>尝试把 JSON 节点读为非 null 字符串。</summary>
    private static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? result) && result is not null)
        {
            value = result;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>解析 LM Studio 默认根目录：~/.lmstudio/.internal/user-concrete-model-default-config。</summary>
    private static string ResolveDefaultRoot()
    {
        string profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(Path.GetFullPath(profile), ".lmstudio", ".internal", "user-concrete-model-default-config");
    }

    /// <summary>稳定读取的文件快照（字节 + 指纹）。</summary>
    private sealed record StableFileSnapshot(byte[] Bytes, FileFingerprint Fingerprint);

    /// <summary>promptTemplate 字段定位结果：索引、节点深拷贝与模板文本。</summary>
    private sealed record PromptField(int Index, JsonObject? Field, string? Template);
}

/// <summary>备份字节保护器抽象（加密/解密）。</summary>
internal interface ILmStudioDefaultsProtector
{
    /// <summary>加密明文字节。</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>解密密文字节。</summary>
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>基于 Windows CurrentUser DPAPI 的备份保护器；非缓冲内存用完即清零。</summary>
internal sealed class WindowsCurrentUserDpapiProtector : ILmStudioDefaultsProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>DPAPI 加密。</summary>
    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    /// <summary>DPAPI 解密。</summary>
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    /// <summary>DPAPI 加解密核心：非托管内存搬运，输出复制后立即释放，输入缓冲用完清零。</summary>
    private static byte[] Transform(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CurrentUser DPAPI 仅在 Windows 上可用。");
        }

        IntPtr inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob(input.Length, inputPointer);
            bool succeeded = protect
                ? CryptProtectData(ref inputBlob, "Codex Multi-Model Manager LM Studio defaults backup", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out DataBlob outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), protect ? "CurrentUser DPAPI 加密失败。" : "CurrentUser DPAPI 解密失败。");
            }

            try
            {
                byte[] output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    LocalFree(outputBlob.Data);
                }
            }
        }
        finally
        {
            // 输入缓冲清零后释放，避免明文残留
            if (input.Length > 0)
            {
                byte[] zeros = new byte[input.Length];
                Marshal.Copy(zeros, 0, inputPointer, input.Length);
            }

            Marshal.FreeHGlobal(inputPointer);
        }
    }

    /// <summary>CRYPTOAPI_BLOB 的托管映射。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
