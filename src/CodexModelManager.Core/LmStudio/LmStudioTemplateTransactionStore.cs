using System.Text.Json;
using System.Text.Json.Serialization;
using CodexModelManager.Core.Infrastructure;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed class LmStudioTemplateTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string directory;

    public LmStudioTemplateTransactionStore(AppPaths paths)
        : this(paths.TransactionsDirectory)
    {
    }

    public LmStudioTemplateTransactionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    public string GetPath(Guid transactionId) => Path.Combine(directory, transactionId.ToString("N") + ".json");

    public string GetEncryptedDefaultsBackupPath(Guid transactionId) =>
        Path.Combine(directory, "encrypted-backups", transactionId.ToString("N") + ".lmstudio-defaults.dpapi");

    public string LifecycleLockPath => Path.Combine(directory, ".lmstudio-lifecycle.lock");

    public async Task WriteAsync(
        LmStudioTemplateTransactionRecord record,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string path = GetPath(record.TransactionId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<LmStudioTemplateTransactionRecord?> ReadAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(transactionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        LmStudioTemplateTransactionRecord? record = await JsonSerializer.DeserializeAsync<LmStudioTemplateTransactionRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (record is null ||
            record.SchemaVersion is not (1 or 2 or 3 or 4) ||
            record.TransactionId != transactionId ||
            !Enum.IsDefined(record.State) ||
            record.LastStableState is not null && !Enum.IsDefined(record.LastStableState.Value) ||
            !Enum.IsDefined(record.FailureStage) ||
            !Enum.IsDefined(record.LastRecoveryFailureStage) ||
            record.RecoveryAttemptCount < 0 ||
            record.OriginalInstance is null ||
            record.OriginalInstance.Endpoint is null ||
            string.IsNullOrWhiteSpace(record.OriginalInstance.SourceModelKey) ||
            string.IsNullOrWhiteSpace(record.OriginalInstance.InstanceId) ||
            record.OriginalInstance.LoadConfiguration?.ContextLength is not > 0 ||
            string.IsNullOrWhiteSpace(record.RuleVersion) ||
            record.FailureCode is not (CompatibilityFailureCodes.LmStudioChatTemplateSystemOrder or
                CompatibilityFailureCodes.LmStudioChatTemplateDeveloperRole or
                CompatibilityFailureCodes.LmStudioChatTemplateContinuationInstructionOrder) ||
            string.IsNullOrWhiteSpace(record.GgufFilePath) ||
            !Path.IsPathFullyQualified(record.GgufFilePath) ||
            string.IsNullOrWhiteSpace(record.GgufFileName) ||
            record.GgufLength <= 0 ||
            record.GgufVersion is 0 ||
            record.OriginalTemplateSha256?.Length != 64 ||
            record.PatchedTemplateSha256?.Length != 64 ||
            (record.SchemaVersion >= 2 && string.IsNullOrWhiteSpace(record.LoadModelKey)) ||
            (record.SchemaVersion >= 2 && !string.Equals(record.LoadModelKey, record.OriginalInstance.SourceModelKey, StringComparison.OrdinalIgnoreCase)) ||
            (record.SchemaVersion >= 3 && !ValidV3Provenance(record)) ||
            (record.SchemaVersion >= 4 && !ValidV4Persistence(record)) ||
            (record.LastApiFailure is not null &&
             (record.LastApiFailure.HttpStatus is < 100 or > 599 ||
              string.IsNullOrWhiteSpace(record.LastApiFailure.Message) ||
              record.LastApiFailure.Message.Length > 512 ||
              record.LastApiFailure.ErrorType?.Length > 96 ||
              record.LastApiFailure.ErrorCode?.Length > 96 ||
              record.LastApiFailure.Parameter?.Length > 96)) ||
            (record.CandidateInstanceId is not null && string.IsNullOrWhiteSpace(record.CandidateInstanceId)) ||
            HasInvalidInstanceIds(record.SameSourceInstanceIdsBeforeLoad) ||
            HasInvalidInstanceIds(record.SameSourceInstanceIdsAfterLoad) ||
            ((record.State is LmStudioTemplateTransactionState.PatchedLoaded or LmStudioTemplateTransactionState.PatchedAndVerified or LmStudioTemplateTransactionState.Completed) &&
             string.IsNullOrWhiteSpace(record.PatchedInstanceId)))
        {
            throw new InvalidDataException($"LM Studio 事务记录无效: {Path.GetFileName(path)}");
        }

        return record;
    }

    public async Task<IReadOnlyList<LmStudioTemplateTransactionRecord>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        CleanupStaleTemporaryFiles();
        List<LmStudioTemplateTransactionRecord> result = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                continue;
            }

            LmStudioTemplateTransactionRecord? record = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                result.Add(record);
            }
        }

        return result.OrderBy(record => record.CreatedAt).ToArray();
    }

    private void CleanupStaleTemporaryFiles()
    {
        DateTime cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(24));
        foreach (string path in Directory.EnumerateFiles(directory, "*.tmp-*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (!IsTransactionTemporaryFileName(name))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static bool IsTransactionTemporaryFileName(string fileName) =>
        fileName.Length == 74 &&
        fileName.AsSpan(32, 10).SequenceEqual(".json.tmp-") &&
        Guid.TryParseExact(fileName.AsSpan(0, 32), "N", out _) &&
        Guid.TryParseExact(fileName.AsSpan(42, 32), "N", out _);

    public async Task<IReadOnlyList<LmStudioTemplateTransactionRecord>> ListCompletedAsync(
        CancellationToken cancellationToken = default) =>
        (await ListAllAsync(cancellationToken).ConfigureAwait(false))
        .Where(record => record.State == LmStudioTemplateTransactionState.Completed)
        .ToArray();

    public async Task<IReadOnlyList<LmStudioTemplateTransactionRecord>> ListIncompleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return (await ListAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(record => record.State is not (LmStudioTemplateTransactionState.Completed or LmStudioTemplateTransactionState.RolledBack))
            .ToArray();
    }

    private static bool ValidV3Provenance(LmStudioTemplateTransactionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TargetRuntimeRuleVersion) ||
            record.OriginalHierarchyProbe is null ||
            !string.Equals(record.OriginalHierarchyProbe.FailureCode, record.FailureCode, StringComparison.Ordinal))
        {
            return false;
        }

        return record.OriginalRuntimeTemplateMode switch
        {
            LmStudioRuntimeTemplateMode.BuiltIn =>
                record.OriginalRuntimeRuleVersion is null &&
                record.OriginalRuntimeTemplateSha256 is null &&
                record.OriginalRuntimeEvidenceTransactionId is null,
            LmStudioRuntimeTemplateMode.ManagerRule =>
                !string.IsNullOrWhiteSpace(record.OriginalRuntimeRuleVersion) &&
                record.OriginalRuntimeTemplateSha256?.Length == 64 &&
                record.OriginalRuntimeEvidenceTransactionId is not null,
            _ => false,
        };
    }

    private static bool HasInvalidInstanceIds(IReadOnlyList<string>? values) =>
        values is not null &&
        (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Count);

    private bool ValidV4Persistence(LmStudioTemplateTransactionRecord record)
    {
        bool supportedVersion = Version.TryParse(record.LmStudioVersion, out Version? version) &&
            version.Major == 0 && version.Minor == 4 && version.Build == 21;
        if (string.IsNullOrWhiteSpace(record.ConcreteModelIdentifier) ||
            Path.IsPathFullyQualified(record.ConcreteModelIdentifier) ||
            !record.ConcreteModelIdentifier.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.PerModelDefaultsPath) ||
            !Path.IsPathFullyQualified(record.PerModelDefaultsPath) ||
            record.OriginalDefaultsFingerprint is null ||
            !record.OriginalDefaultsFingerprint.Exists ||
            record.OriginalDefaultsFingerprint.Length <= 0 ||
            record.OriginalDefaultsFingerprint.Sha256?.Length != 64 ||
            record.OriginalPersistentTemplateState is null ||
            !string.Equals(record.TargetPersistentRuleVersion, PromptTemplateRepairService.CurrentRuleVersion, StringComparison.Ordinal) ||
            record.TargetPersistentTemplateSha256?.Length != 64 ||
            !record.TargetPersistentTemplateSha256.Equals(record.PatchedTemplateSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.TargetPersistentRuleVersion, record.RuleVersion, StringComparison.Ordinal) ||
            !string.Equals(record.TargetPersistentRuleVersion, record.TargetRuntimeRuleVersion, StringComparison.Ordinal) ||
             record.CandidateDefaultsSha256?.Length != 64 ||
             string.IsNullOrWhiteSpace(record.LmStudioVersion) ||
             !supportedVersion ||
             !Enum.IsDefined(record.PersistenceStage) ||
             !ValidV4StageState(record))
        {
            return false;
        }

        bool originalStateValid = record.OriginalPersistentTemplateState switch
        {
            LmStudioPersistentTemplateFieldState.Missing => record.OriginalPersistentRuleVersion is null && record.OriginalPersistentTemplateSha256 is null,
            LmStudioPersistentTemplateFieldState.ManagerV2 =>
                string.Equals(record.OriginalPersistentRuleVersion, PromptTemplateRepairService.LegacyLeadingRuleVersion, StringComparison.Ordinal) &&
                record.OriginalPersistentTemplateSha256?.Length == 64,
            LmStudioPersistentTemplateFieldState.ManagerV3 =>
                string.Equals(record.OriginalPersistentRuleVersion, PromptTemplateRepairService.CurrentRuleVersion, StringComparison.Ordinal) &&
                record.OriginalPersistentTemplateSha256?.Equals(record.TargetPersistentTemplateSha256, StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        };
        if (!originalStateValid)
        {
            return false;
        }

        bool backupRequired = record.PersistenceStage >= LmStudioPersistenceStage.BackupVerified;
        if (backupRequired &&
            (string.IsNullOrWhiteSpace(record.EncryptedDefaultsBackupPath) ||
             !Path.IsPathFullyQualified(record.EncryptedDefaultsBackupPath) ||
             record.DefaultsBackupPlaintextSha256?.Length != 64 ||
             !record.DefaultsBackupPlaintextSha256.Equals(record.OriginalDefaultsFingerprint.Sha256, StringComparison.OrdinalIgnoreCase) ||
             !BackupPathMatches(record)))
        {
            return false;
        }

        return record.State != LmStudioTemplateTransactionState.Completed ||
            record.PersistenceStage == LmStudioPersistenceStage.PersistentDefaultVerified;
    }

    private static bool ValidV4StageState(LmStudioTemplateTransactionRecord record)
    {
        LmStudioPersistenceStage stage = record.PersistenceStage;
        if (stage == LmStudioPersistenceStage.None ||
            stage == LmStudioPersistenceStage.RecoveryBlocked && record.State != LmStudioTemplateTransactionState.RecoveryBlocked)
        {
            return false;
        }

        return record.State switch
        {
            LmStudioTemplateTransactionState.Prepared => stage is
                LmStudioPersistenceStage.Prepared or
                LmStudioPersistenceStage.BackupVerified or
                LmStudioPersistenceStage.DefaultsVerified or
                LmStudioPersistenceStage.Restored,
            LmStudioTemplateTransactionState.OriginalUnloaded => stage is
                LmStudioPersistenceStage.DefaultsVerified or
                LmStudioPersistenceStage.Restored,
            LmStudioTemplateTransactionState.PatchedLoaded => stage is
                LmStudioPersistenceStage.DefaultsVerified or
                LmStudioPersistenceStage.PersistentDefaultVerified or
                LmStudioPersistenceStage.Restored,
            LmStudioTemplateTransactionState.PatchedAndVerified => stage is
                LmStudioPersistenceStage.PersistentDefaultVerified or
                LmStudioPersistenceStage.Restored,
            LmStudioTemplateTransactionState.RolledBack => stage is
                LmStudioPersistenceStage.Prepared or
                LmStudioPersistenceStage.Restored,
            LmStudioTemplateTransactionState.RecoveryBlocked => stage is
                LmStudioPersistenceStage.RecoveryBlocked or
                LmStudioPersistenceStage.Restored,
            LmStudioTemplateTransactionState.Completed => stage == LmStudioPersistenceStage.PersistentDefaultVerified,
            LmStudioTemplateTransactionState.RollbackFailed => true,
            _ => false,
        };
    }

    private bool BackupPathMatches(LmStudioTemplateTransactionRecord record)
    {
        try
        {
            return Path.GetFullPath(record.EncryptedDefaultsBackupPath!)
                .Equals(GetEncryptedDefaultsBackupPath(record.TransactionId), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
