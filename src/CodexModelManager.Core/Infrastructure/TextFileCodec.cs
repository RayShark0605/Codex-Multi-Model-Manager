using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

public static class TextFileCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<TextFileSnapshot> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new TextFileSnapshot(
                path,
                [],
                string.Empty,
                new TextFileFormat(false, Environment.NewLine, false, false),
                FileFingerprint.Missing);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var content = hasBom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes.AsSpan();
        var text = StrictUtf8.GetString(content);
        var format = DetectFormat(text, hasBom);
        var fileInfo = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var fingerprint = new FileFingerprint(
            true,
            bytes.LongLength,
            fileInfo.LastWriteTimeUtc,
            hash);
        return new TextFileSnapshot(path, bytes, text, format, fingerprint);
    }

    public static byte[] Encode(string text, TextFileFormat format)
    {
        var content = StrictUtf8.GetBytes(text);
        if (!format.HasUtf8Bom)
        {
            return content;
        }

        var result = new byte[Encoding.UTF8.Preamble.Length + content.Length];
        Encoding.UTF8.Preamble.CopyTo(result);
        content.CopyTo(result, Encoding.UTF8.Preamble.Length);
        return result;
    }

    public static TextFileFormat DetectFormat(string text, bool hasBom = false)
    {
        var crlf = 0;
        var lf = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            if (index > 0 && text[index - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        var newLine = crlf >= lf && crlf > 0 ? "\r\n" : "\n";
        if (crlf == 0 && lf == 0)
        {
            newLine = Environment.NewLine;
        }

        return new TextFileFormat(
            hasBom,
            newLine,
            text.EndsWith('\n'),
            crlf > 0 && lf > 0);
    }
}
