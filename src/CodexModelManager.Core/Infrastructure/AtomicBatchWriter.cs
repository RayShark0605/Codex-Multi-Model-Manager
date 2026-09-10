using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 原子批量写入器：把一组“预演过的文件变更”作为一个事务写入——
/// 命名互斥体串行化多实例并发；先落临时文件并校验哈希，再以 File.Replace/Move 原子提交；
/// 提交后复核内容，失败时自动回滚到原始状态，任何情况下都清理临时与回滚文件。
/// </summary>
public sealed class AtomicBatchWriter : IAtomicBatchWriter
{
    private const string MutexName = "Local\\CodexMultiModelManager.ConfigWriter.v1";
    private static readonly TimeSpan WriterWaitTimeout = TimeSpan.FromSeconds(15);
    private readonly IAvailableDiskSpaceProvider diskSpaceProvider;
    private readonly string mutexName;

    /// <summary>默认构造：使用 Windows 磁盘空间查询与全局命名互斥体。</summary>
    public AtomicBatchWriter()
        : this(new WindowsAvailableDiskSpaceProvider(), MutexName)
    {
    }

    /// <summary>测试用构造：可注入磁盘空间提供者与自定义互斥体名。</summary>
    internal AtomicBatchWriter(IAvailableDiskSpaceProvider diskSpaceProvider, string? mutexName = null)
    {
        this.diskSpaceProvider = diskSpaceProvider ?? throw new ArgumentNullException(nameof(diskSpaceProvider));
        this.mutexName = string.IsNullOrWhiteSpace(mutexName) ? MutexName : mutexName;
    }

    /// <summary>
    /// 在互斥体属主线程上执行整个事务（见 <see cref="WriteOnMutexOwnerThread"/> 的说明），
    /// 对调用方呈现为普通异步操作。
    /// </summary>
    public async Task WriteAsync(IReadOnlyList<PlannedFileChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        // Windows 互斥体的属主是获取它的线程。把“获取互斥体、在专属线程上同步桥接
        // 异步事务、释放互斥体”三步固定在同一条专用线程上，被抛弃的属主才能被安全恢复。
        await Task.Factory.StartNew(() => WriteOnMutexOwnerThread(changes, cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
    }

    /// <summary>
    /// 互斥体属主线程的同步执行体：等待互斥体或取消信号（15 秒超时），
    /// 成功获取后在持锁状态下同步运行异步事务；AbandonedMutexException 视为获得了所有权。
    /// </summary>
    private void WriteOnMutexOwnerThread(IReadOnlyList<PlannedFileChange> changes, CancellationToken cancellationToken)
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
                    // 所有权已转移给本线程；下方所有常规指纹与语义校验仍会照常执行后才做任何修改。
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

    /// <summary>
    /// 持锁事务主体：排序（CommitLast 靠后）并查重目标 → 首轮指纹校验 → 磁盘空间校验 →
    /// 逐项写临时文件并校验 → 锁定现存目标文件 → 提交前二次指纹校验 → 原子替换/移动 →
    /// 逐项复核提交结果 → 清理回滚副本；任何一步失败都按逆序回滚已提交项。
    /// </summary>
    private async Task WriteUnderLockAsync(IReadOnlyList<PlannedFileChange> changes, CancellationToken cancellationToken)
    {
        PlannedFileChange[] ordered = changes.OrderBy(change => change.CommitLast ? 1 : 0).ToArray();
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
                string? tempPath = change.CandidateBytes is null ? null : Path.Combine(directory, $".{Path.GetFileName(fullPath)}.cmm-{token}.tmp");
                string rollbackPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.cmm-{token}.rollback");
                // 在第一个可能产生半成品临时文件或在校验期间失败/取消的操作之前登记清理责任。
                staged.Add(new StagedChange(change, fullPath, tempPath, rollbackPath));
                if (change.CandidateBytes is not null)
                {
                    await WriteTempAsync(tempPath!, change.CandidateBytes, cancellationToken).ConfigureAwait(false);
                    if (change.Validator is not null)
                    {
                        await change.Validator(change.CandidateBytes).ConfigureAwait(false);
                    }

                    byte[] stagedBytes = await File.ReadAllBytesAsync(tempPath!, cancellationToken).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stagedBytes), SHA256.HashData(change.CandidateBytes)))
                    {
                        throw new IOException($"临时文件校验失败: {Path.GetFileName(fullPath)}");
                    }
                }
            }

            // 锁定现存目标文件以阻止并发写入者，同时仍允许 File.Replace/File.Move
            // 完成原子改名；尚不存在的目标由下方“不覆盖式 File.Move”保护。
            targetLocks = AcquireTargetLocks(ordered);

            // 尽量推迟到提交前、并在目标文件已被写锁定时，关闭预演与提交之间的竞态窗口。
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
            // File.Replace 之后，打开在旧目标上的句柄会跟着该文件“漂移”到回滚路径。
            // 先释放这些句柄，再在恢复原件期间锁定当前可见的候选文件。
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
                throw new AggregateException("配置写入失败，且自动回滚未能完全完成。原始故障与回滚故障均已保留。", primaryException, rollbackException);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw new InvalidOperationException("Unreachable");
        }
        finally
        {
            DisposeLocks(targetLocks);

            foreach (StagedChange item in staged)
            {
                if (item.TempPath is not null)
                {
                    SafeDelete(item.TempPath);
                }

                if (cleanupRollbackFiles)
                {
                    SafeDelete(item.RollbackPath);
                }
            }
        }
    }

    /// <summary>释放全部目标文件锁（容错：单个 Dispose 失败不影响其余）。</summary>
    private static void DisposeLocks(IEnumerable<FileStream> locks)
    {
        foreach (FileStream targetLock in locks)
        {
            targetLock.Dispose();
        }
    }

    /// <summary>
    /// 以只读共享方式锁定全部现存目标文件；按路径排序保证多实例间的锁顺序一致，避免死锁。
    /// 任一锁定失败则释放已持有的全部锁并抛出。
    /// </summary>
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
            foreach (FileStream targetLock in locks)
            {
                targetLock.Dispose();
            }

            throw;
        }
    }

    /// <summary>提交后复核：删除项确认目标已消失；写入项确认字节哈希一致且业务校验器通过。</summary>
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

    /// <summary>按提交的逆序回滚：原存在的用回滚副本恢复，原本不存在的删除到缺失状态；失败项聚合抛出。</summary>
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
                    // 恢复“原本不存在”的基线属于事务性操作而非尽力清理，绝不掩盖删除失败。
                    File.Delete(item.TargetPath);
                    if (File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath))
                    {
                        throw new IOException($"事务回滚未能恢复目标缺失状态: {item.TargetPath}");
                    }
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

    /// <summary>逐项采集当前指纹并与计划中的期望指纹比对，任何不一致即失败（要求调用方重新加载）。</summary>
    private static async Task VerifyFingerprintsAsync(IEnumerable<PlannedFileChange> changes, CancellationToken cancellationToken)
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

    /// <summary>把候选字节写入临时文件（CreateNew + WriteThrough，杜绝复用旧文件）。</summary>
    private static async Task WriteTempAsync(string tempPath, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }

    /// <summary>
    /// 按磁盘分区预检空间：候选字节 + 原文件大小 + 4 MB 裕量，可用空间不足时在动手前失败。
    /// </summary>
    internal static void VerifyAvailableSpace(IEnumerable<PlannedFileChange> changes, IAvailableDiskSpaceProvider diskSpaceProvider)
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

    /// <summary>校验事务不含重复目标路径（忽略大小写），存在重复即抛出。</summary>
    private static void EnsureUniqueTargets(IEnumerable<PlannedFileChange> changes)
    {
        string? duplicate = changes.GroupBy(change => Path.GetFullPath(change.Path), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"事务包含重复目标: {duplicate}");
        }
    }

    /// <summary>尽力删除临时/回滚文件；失败时保留文件比掩盖主错误更安全。</summary>
    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 残留一个旧的回滚/临时文件，好过把它背后的主错误藏起来
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>已进入事务的变更项：原始计划、目标/临时/回滚路径与提交时目标是否存在的标记。</summary>
    private sealed record StagedChange(PlannedFileChange Change, string TargetPath, string? TempPath, string RollbackPath, bool OriginalExisted = false);
}

/// <summary>磁盘可用空间查询结果：卷是否就绪与可用字节数。</summary>
internal readonly record struct AvailableDiskSpace(bool IsReady, long AvailableBytes);

/// <summary>磁盘可用空间提供者抽象（便于测试注入）。</summary>
internal interface IAvailableDiskSpaceProvider
{
    /// <summary>查询 <paramref name="rootPath"/> 所在卷的可用空间。</summary>
    AvailableDiskSpace GetAvailableSpace(string rootPath);
}

/// <summary>Windows 磁盘可用空间查询：UNC 路径走 GetDiskFreeSpaceEx，本地路径走 DriveInfo。</summary>
internal sealed class WindowsAvailableDiskSpaceProvider : IAvailableDiskSpaceProvider
{
    /// <summary>查询卷可用空间；超过 long.MaxValue 时截断为 long.MaxValue。</summary>
    public AvailableDiskSpace GetAvailableSpace(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (OperatingSystem.IsWindows() && rootPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            // GetDiskFreeSpaceEx 要求 UNC 路径以分隔符结尾
            string queryPath = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
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

    /// <summary>GetDiskFreeSpaceEx 的 P/Invoke 声明。</summary>
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
    }
}
