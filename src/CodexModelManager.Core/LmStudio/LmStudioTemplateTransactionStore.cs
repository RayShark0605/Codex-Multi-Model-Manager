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
            record.SchemaVersion is not (1 or 2 or 3) ||
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

        List<LmStudioTemplateTransactionRecord> result = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                throw new InvalidDataException($"LM Studio 事务目录包含无法识别的 JSON 记录: {Path.GetFileName(path)}");
            }

            LmStudioTemplateTransactionRecord? record = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                result.Add(record);
            }
        }

        return result.OrderBy(record => record.CreatedAt).ToArray();
    }

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
}
