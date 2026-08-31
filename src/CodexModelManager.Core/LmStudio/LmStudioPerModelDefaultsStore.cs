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

public sealed class LmStudioPerModelDefaultsStore
{
    public const string PromptTemplateKey = "llm.load.promptTemplate";
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumJsonDepth = 32;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly IPromptTemplateRepairService templateRepair;
    private readonly IAtomicBatchWriter atomicWriter;
    private readonly ILmStudioDefaultsProtector protector;
    private readonly string rootDirectory;

    public LmStudioPerModelDefaultsStore(
        IPromptTemplateRepairService templateRepair,
        IAtomicBatchWriter atomicWriter)
        : this(templateRepair, atomicWriter, new WindowsCurrentUserDpapiProtector(), ResolveDefaultRoot())
    {
    }

    internal LmStudioPerModelDefaultsStore(
        IPromptTemplateRepairService templateRepair,
        IAtomicBatchWriter atomicWriter,
        ILmStudioDefaultsProtector protector,
        string rootDirectory)
    {
        this.templateRepair = templateRepair ?? throw new ArgumentNullException(nameof(templateRepair));
        this.atomicWriter = atomicWriter ?? throw new ArgumentNullException(nameof(atomicWriter));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory => rootDirectory;

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

    public async Task<LmStudioDefaultsBackupArtifact> CreateVerifiedBackupAsync(
        LmStudioPerModelDefaultsPlan plan,
        string backupPath,
        CancellationToken cancellationToken = default)
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

    public async Task ApplyAsync(
        LmStudioPerModelDefaultsPlan plan,
        CancellationToken cancellationToken = default)
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

    public static async Task<FileFingerprint> VerifyAppliedAsync(
        LmStudioPerModelDefaultsPlan plan,
        CancellationToken cancellationToken = default)
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

    public async Task<LmStudioDefaultsRestoreResult> RestoreAsync(
        LmStudioPerModelDefaultsPlan plan,
        LmStudioDefaultsBackupArtifact backup,
        CancellationToken cancellationToken = default)
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

    public async Task<LmStudioDefaultsRestoreResult> RestoreFromTransactionAsync(
        LmStudioTemplateTransactionRecord record,
        GgufChatTemplateAnalysis analysis,
        CancellationToken cancellationToken = default)
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

    public static async Task<FileFingerprint> VerifyTransactionTargetAsync(
        LmStudioTemplateTransactionRecord record,
        CancellationToken cancellationToken = default)
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

    private (LmStudioPersistentTemplateFieldState State, string? RuleVersion, string? TemplateSha256) ClassifyOriginalField(
        PromptField field,
        GgufChatTemplateAnalysis analysis,
        PromptTemplateRepairPreview targetPreview,
        LmStudioRuntimeTemplateProvenance runtimeProvenance)
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

    private static JsonObject ParseAndValidateRoot(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException($"LM Studio per-model defaults 文件大小必须在 1 到 {MaximumFileBytes:N0} 字节之间。");
        }

        JsonNode? node = JsonNode.Parse(
            bytes,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumJsonDepth });
        if (node is not JsonObject root || root["preset"] is not JsonValue preset || !preset.TryGetValue(out string? _))
        {
            throw new InvalidDataException("LM Studio per-model defaults 根必须是 object，且 preset 必须是 string。");
        }

        ValidateFieldsContainer(root, "operation");
        ValidateFieldsContainer(root, "load");
        _ = ReadPromptField(root);
        return root;
    }

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

    private static JsonObject CreateTargetPromptField(string template) => new()
    {
        ["key"] = PromptTemplateKey,
        ["value"] = new JsonObject
        {
            ["type"] = "jinja",
            ["jinjaPromptTemplate"] = new JsonObject { ["template"] = template },
        },
    };

    private static bool PromptFieldsEqual(PromptField left, PromptField right) =>
        left.Template is null && right.Template is null ||
        left.Template is not null && right.Template is not null && left.Template.Equals(right.Template, StringComparison.Ordinal) && JsonNode.DeepEquals(left.Field, right.Field);

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

    private static JsonArray GetLoadFields(JsonObject root) =>
        (JsonArray)((JsonObject)root["load"]!)["fields"]!;

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

    private static void ValidateTargetCandidate(byte[] bytes, string expectedTemplateSha256)
    {
        JsonObject root = ParseAndValidateRoot(bytes);
        PromptField prompt = ReadPromptField(root);
        if (prompt.Template is null || !ComputeTemplateSha(prompt.Template).Equals(expectedTemplateSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("候选 defaults 未包含目标 v3 Prompt Template SHA。");
        }
    }

    private static byte[] Serialize(JsonObject root) => Utf8NoBom.GetBytes(root.ToJsonString(WriteOptions) + Environment.NewLine);

    private static FileFingerprint FingerprintCandidate(byte[] bytes) =>
        new(true, bytes.LongLength, null, Convert.ToHexString(SHA256.HashData(bytes)));

    private static string ComputeTemplateSha(string template) => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(template)));

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
            throw new NotSupportedException($"LM Studio {lmStudioVersion ?? "unknown"} 的 per-model defaults 格式未经验证；当前仅支持 {LmStudioPerModelDefaultsCompatibility.SupportedVersionFamilies}。");
        }
    }

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

    private static string NormalizeConcreteIdentifier(string value) => string.Join('/', ValidateConcreteIdentifier(value));

    private static bool IsUnderRoot(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

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

    private static string ResolveDefaultRoot()
    {
        string profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(Path.GetFullPath(profile), ".lmstudio", ".internal", "user-concrete-model-default-config");
    }

    private sealed record StableFileSnapshot(byte[] Bytes, FileFingerprint Fingerprint);
    private sealed record PromptField(int Index, JsonObject? Field, string? Template);
}

internal interface ILmStudioDefaultsProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

internal sealed class WindowsCurrentUserDpapiProtector : ILmStudioDefaultsProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

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
            if (input.Length > 0)
            {
                byte[] zeros = new byte[input.Length];
                Marshal.Copy(zeros, 0, inputPointer, input.Length);
            }

            Marshal.FreeHGlobal(inputPointer);
        }
    }

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
