using System.Security.Cryptography;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 文件指纹服务：以“存在性 + 长度 + 最后写入时间 + SHA-256”四元组刻画文件状态，
/// 用于切换流程中检测配置文件是否被外部修改。
/// </summary>
public static class FileFingerprintService
{
    /// <summary>异步计算文件指纹；文件不存在时返回 <see cref="FileFingerprint.Missing"/>。</summary>
    public static async Task<FileFingerprint> CaptureAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return FileFingerprint.Missing;
        }

        // 以共享读方式打开，避免与其他持有该文件的进程冲突；异步 + 顺序扫描适合整读
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(path);
        return new FileFingerprint(true, info.Length, info.LastWriteTimeUtc, Convert.ToHexString(hash));
    }

    /// <summary>判断两个指纹是否一致（哈希比较忽略大小写，兼容大小写十六进制）。</summary>
    public static bool Matches(FileFingerprint expected, FileFingerprint actual) =>
        expected.Exists == actual.Exists
        && expected.Length == actual.Length
        && string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase)
        && expected.LastWriteTimeUtc == actual.LastWriteTimeUtc;
}
