using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public sealed class AtomicBatchWriter : IAtomicBatchWriter
{
    private const string MutexName = "Local\\CodexMultiModelManager.ConfigWriter.v1";
    private static readonly TimeSpan WriterWaitTimeout = TimeSpan.FromSeconds(15);
    private readonly IAvailableDiskSpaceProvider diskSpaceProvider;
    private readonly string mutexName;

    public AtomicBatchWriter()
        : this(new WindowsAvailableDiskSpaceProvider(), MutexName)
    {
    }

    internal AtomicBatchWriter(IAvailableDiskSpaceProvider diskSpaceProvider, string? mutexName = null)
    {
        this.diskSpaceProvider = diskSpaceProvider ?? throw new ArgumentNullException(nameof(diskSpaceProvider));
        this.mutexName = string.IsNullOrWhiteSpace(mutexName) ? MutexName : mutexName;
    }

    public async Task WriteAsync(
        IReadOnlyList<PlannedFileChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        // A Windows mutex is owned by the acquiring thread. Keep acquisition,
        // the synchronous bridge over the async transaction, and release on one
        // dedicated thread so an abandoned owner can be recovered safely.
        await Task.Factory.StartNew(
            () => WriteOnMutexOwnerThread(changes, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    private void WriteOnMutexOwnerThread(
        IReadOnlyList<PlannedFileChange> changes,
        CancellationToken cancellationToken)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, mutexName);
        }
        catch (WaitHandleCannotBeOpenedException exception)
        {
            throw new IOException("检测到旧版本正在使用不兼容的配置写入锁。请关闭所有旧版本 Codex Multi-Model Manager 实例后重试。", exception);
        }

        using (mutex)
        {
            bool acquired = false;
            try
            {
                int waitResult;
                try
                {
                    waitResult = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], WriterWaitTimeout);
                }
                catch (AbandonedMutexException exception) when (exception.MutexIndex is 0 or -1)
                {
                    // Ownership transfers to this thread. All ordinary fingerprint
                    // and semantic checks still run below before any mutation.
                    waitResult = 0;
                }

                if (waitResult == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                acquired = waitResult == 0;
                if (!acquired)
                {
                    throw new IOException("另一个 Codex Multi-Model Manager 实例正在写配置。");
                }

                WriteUnderLockAsync(changes, cancellationToken).GetAwaiter().GetResult();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private async Task WriteUnderLockAsync(
        IReadOnlyList<PlannedFileChange> changes,
        CancellationToken cancellationToken)
    {
        PlannedFileChange[] ordered = changes
            .OrderBy(change => change.CommitLast ? 1 : 0)
            .ToArray();
        EnsureUniqueTargets(ordered);
        await VerifyFingerprintsAsync(ordered, cancellationToken).ConfigureAwait(false);
        VerifyAvailableSpace(ordered, diskSpaceProvider);

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
        catch (Exception primaryException)
        {
            cleanupRollbackFiles = false;
            // After File.Replace, a handle opened on the old target follows that
            // file to the rollback path. Release those handles, then lock the
            // currently visible candidate files while restoring the originals.
            DisposeLocks(targetLocks);
            targetLocks.Clear();
            List<FileStream> rollbackTargetLocks = [];
            Exception? rollbackException = null;
            try
            {
                rollbackTargetLocks = AcquireTargetLocks(committed.Select(item => item.Change));
                RollBack(committed);
                cleanupRollbackFiles = true;
            }
            catch (Exception exception)
            {
                rollbackException = exception;
            }
            finally
            {
                DisposeLocks(rollbackTargetLocks);
            }

            if (rollbackException is not null)
            {
                throw new AggregateException(
                    "配置写入失败，且自动回滚未能完全完成。原始故障与回滚故障均已保留。",
                    primaryException,
                    rollbackException);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw new InvalidOperationException("Unreachable");
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

    internal static void VerifyAvailableSpace(
        IEnumerable<PlannedFileChange> changes,
        IAvailableDiskSpaceProvider diskSpaceProvider)
    {
        foreach (IGrouping<string, PlannedFileChange> group in changes.GroupBy(change => Path.GetPathRoot(Path.GetFullPath(change.Path))!, StringComparer.OrdinalIgnoreCase))
        {
            long required = group.Sum(change => (change.CandidateBytes?.LongLength ?? 0) + (change.ExpectedFingerprint.Exists ? change.ExpectedFingerprint.Length : 0));
            AvailableDiskSpace available = diskSpaceProvider.GetAvailableSpace(group.Key);
            if (available.IsReady && available.AvailableBytes < required + (4L * 1024 * 1024))
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

internal readonly record struct AvailableDiskSpace(bool IsReady, long AvailableBytes);

internal interface IAvailableDiskSpaceProvider
{
    AvailableDiskSpace GetAvailableSpace(string rootPath);
}

internal sealed class WindowsAvailableDiskSpaceProvider : IAvailableDiskSpaceProvider
{
    public AvailableDiskSpace GetAvailableSpace(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (OperatingSystem.IsWindows() && rootPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            string queryPath = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;
            if (!NativeMethods.GetDiskFreeSpaceEx(queryPath, out ulong available, out _, out _))
            {
                var inner = new Win32Exception(Marshal.GetLastWin32Error());
                throw new IOException($"无法查询 UNC 路径可用空间: {rootPath}", inner);
            }

            return new AvailableDiskSpace(true, available > long.MaxValue ? long.MaxValue : (long)available);
        }

        var drive = new DriveInfo(rootPath);
        return new AvailableDiskSpace(drive.IsReady, drive.IsReady ? drive.AvailableFreeSpace : 0);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);
    }
}
