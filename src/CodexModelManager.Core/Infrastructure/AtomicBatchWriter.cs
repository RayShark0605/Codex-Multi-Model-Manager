using System.Security.Cryptography;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public sealed class AtomicBatchWriter : IAtomicBatchWriter
{
    private const string MutexName = "Local\\CodexMultiModelManager.ConfigWriter.v1";

    public async Task WriteAsync(
        IReadOnlyList<PlannedFileChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        using var semaphore = new Semaphore(1, 1, MutexName);
        bool acquired = false;
        try
        {
            int waitResult = await Task.Run(
                () => WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle], TimeSpan.FromSeconds(15)),
                CancellationToken.None).ConfigureAwait(false);
            if (waitResult == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            acquired = waitResult == 0;
            if (!acquired)
            {
                throw new IOException("另一个 Codex Multi-Model Manager 实例正在写配置。");
            }

            await WriteUnderLockAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                semaphore.Release();
            }
        }
    }

    private static async Task WriteUnderLockAsync(
        IReadOnlyList<PlannedFileChange> changes,
        CancellationToken cancellationToken)
    {
        PlannedFileChange[] ordered = changes
            .OrderBy(change => change.CommitLast ? 1 : 0)
            .ToArray();
        EnsureUniqueTargets(ordered);
        await VerifyFingerprintsAsync(ordered, cancellationToken).ConfigureAwait(false);
        VerifyAvailableSpace(ordered);

        List<StagedChange> staged = [];
        List<StagedChange> committed = [];
        List<FileStream> targetLocks = [];
        bool cleanupRollbackFiles = true;
        try
        {
            foreach (PlannedFileChange change in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(change.Path);
                string? directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new InvalidOperationException($"无效配置路径: {fullPath}");
                }

                Directory.CreateDirectory(directory);
                string token = Guid.NewGuid().ToString("N");
                string? tempPath = null;
                if (change.CandidateBytes is not null)
                {
                    tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.cmm-{token}.tmp");
                    await WriteTempAsync(tempPath, change.CandidateBytes, cancellationToken).ConfigureAwait(false);
                    if (change.Validator is not null)
                    {
                        await change.Validator(change.CandidateBytes).ConfigureAwait(false);
                    }

                    byte[] stagedBytes = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stagedBytes), SHA256.HashData(change.CandidateBytes)))
                    {
                        throw new IOException($"临时文件校验失败: {Path.GetFileName(fullPath)}");
                    }
                }

                string rollbackPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.cmm-{token}.rollback");
                staged.Add(new StagedChange(change, fullPath, tempPath, rollbackPath));
            }

            // Deny concurrent writers while still allowing File.Replace/File.Move to
            // atomically rename the target. Missing targets remain protected by the
            // non-overwriting File.Move used below.
            targetLocks = AcquireTargetLocks(ordered);

            // Close the race between preview and commit as late as possible and while
            // the existing target files are write-locked.
            await VerifyFingerprintsAsync(ordered, cancellationToken).ConfigureAwait(false);

            foreach (StagedChange item in staged)
            {
                bool existed = File.Exists(item.TargetPath);
                if (item.Change.CandidateBytes is null)
                {
                    if (existed)
                    {
                        File.Move(item.TargetPath, item.RollbackPath);
                    }
                }
                else if (existed)
                {
                    File.Replace(item.TempPath!, item.TargetPath, item.RollbackPath, true);
                }
                else
                {
                    File.Move(item.TempPath!, item.TargetPath);
                }

                committed.Add(item with { OriginalExisted = existed });
            }

            foreach (StagedChange item in committed)
            {
                await VerifyCommittedAsync(item, cancellationToken).ConfigureAwait(false);
            }

            foreach (StagedChange item in committed)
            {
                SafeDelete(item.RollbackPath);
            }
        }
        catch
        {
            cleanupRollbackFiles = false;
            // After File.Replace, a handle opened on the old target follows that
            // file to the rollback path. Release those handles, then lock the
            // currently visible candidate files while restoring the originals.
            DisposeLocks(targetLocks);
            targetLocks.Clear();
            List<FileStream> rollbackTargetLocks = [];
            try
            {
                rollbackTargetLocks = AcquireTargetLocks(committed.Select(item => item.Change));
                RollBack(committed);
                cleanupRollbackFiles = true;
            }
            finally
            {
                DisposeLocks(rollbackTargetLocks);
            }

            throw;
        }
        finally
        {
            DisposeLocks(targetLocks);

            foreach (StagedChange item in staged)
            {
                if (item.TempPath is not null) SafeDelete(item.TempPath);
                if (cleanupRollbackFiles) SafeDelete(item.RollbackPath);
            }
        }
    }

    private static void DisposeLocks(IEnumerable<FileStream> locks)
    {
        foreach (FileStream targetLock in locks) targetLock.Dispose();
    }

    private static List<FileStream> AcquireTargetLocks(IEnumerable<PlannedFileChange> changes)
    {
        List<FileStream> locks = [];
        try
        {
            foreach (string path in changes.Select(change => Path.GetFullPath(change.Path)).Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase))
            {
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            }

            return locks;
        }
        catch
        {
            foreach (FileStream targetLock in locks) targetLock.Dispose();
            throw;
        }
    }

    private static async Task VerifyCommittedAsync(StagedChange item, CancellationToken cancellationToken)
    {
        if (item.Change.CandidateBytes is null)
        {
            if (File.Exists(item.TargetPath))
            {
                throw new IOException($"删除后验证失败: {Path.GetFileName(item.TargetPath)}");
            }

            return;
        }

        byte[] actual = await File.ReadAllBytesAsync(item.TargetPath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(item.Change.CandidateBytes)))
        {
            throw new IOException($"提交后 SHA-256 校验失败: {Path.GetFileName(item.TargetPath)}");
        }

        if (item.Change.Validator is not null)
        {
            await item.Change.Validator(actual).ConfigureAwait(false);
        }
    }

    private static void RollBack(List<StagedChange> committed)
    {
        List<Exception> failures = [];
        foreach (StagedChange item in committed.AsEnumerable().Reverse())
        {
            try
            {
                if (item.OriginalExisted)
                {
                    if (!File.Exists(item.RollbackPath))
                    {
                        throw new IOException($"事务回滚副本缺失: {item.RollbackPath}");
                    }

                    if (File.Exists(item.TargetPath))
                    {
                        File.Replace(item.RollbackPath, item.TargetPath, null, true);
                    }
                    else
                    {
                        File.Move(item.RollbackPath, item.TargetPath);
                    }
                }
                else
                {
                    SafeDelete(item.TargetPath);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("配置写入失败，且自动回滚未能完全完成。请从历史快照恢复。", failures);
        }
    }

    private static async Task VerifyFingerprintsAsync(
        IEnumerable<PlannedFileChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (PlannedFileChange change in changes)
        {
            FileFingerprint actual = await FileFingerprintService.CaptureAsync(change.Path, cancellationToken).ConfigureAwait(false);
            if (!FileFingerprintService.Matches(change.ExpectedFingerprint, actual))
            {
                throw new IOException($"配置文件在操作期间发生变化，请重新加载: {Path.GetFileName(change.Path)}");
            }
        }
    }

    private static async Task WriteTempAsync(string tempPath, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }

    private static void VerifyAvailableSpace(IEnumerable<PlannedFileChange> changes)
    {
        foreach (IGrouping<string, PlannedFileChange> group in changes.GroupBy(change => Path.GetPathRoot(Path.GetFullPath(change.Path))!, StringComparer.OrdinalIgnoreCase))
        {
            long required = group.Sum(change => (change.CandidateBytes?.LongLength ?? 0) + (change.ExpectedFingerprint.Exists ? change.ExpectedFingerprint.Length : 0));
            var drive = new DriveInfo(group.Key);
            if (drive.IsReady && drive.AvailableFreeSpace < required + (4L * 1024 * 1024))
            {
                throw new IOException($"磁盘空间不足，事务至少还需要 {required:N0} 字节。");
            }
        }
    }

    private static void EnsureUniqueTargets(IEnumerable<PlannedFileChange> changes)
    {
        string? duplicate = changes
            .GroupBy(change => Path.GetFullPath(change.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"事务包含重复目标: {duplicate}");
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A stale rollback/temp file is safer than hiding the primary error.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record StagedChange(
        PlannedFileChange Change,
        string TargetPath,
        string? TempPath,
        string RollbackPath,
        bool OriginalExisted = false);
}
