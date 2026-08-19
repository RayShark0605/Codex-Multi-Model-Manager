using System.Security.Cryptography;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public static class FileFingerprintService
{
    public static async Task<FileFingerprint> CaptureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return FileFingerprint.Missing;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(path);
        return new FileFingerprint(true, info.Length, info.LastWriteTimeUtc, Convert.ToHexString(hash));
    }

    public static bool Matches(FileFingerprint expected, FileFingerprint actual) =>
        expected.Exists == actual.Exists
        && expected.Length == actual.Length
        && string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase)
        && expected.LastWriteTimeUtc == actual.LastWriteTimeUtc;
}
