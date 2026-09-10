using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.Infrastructure;

/// <summary>
/// 文本文件编解码器：以严格 UTF-8 读取文件并保留格式信息（BOM、换行风格、末尾换行、混合换行），
/// 写出时按原格式还原，保证对 Codex 配置文件的改动不引入无关差异。
/// </summary>
public static class TextFileCodec
{
    // 不发射 BOM 且遇到非法字节抛异常的严格 UTF-8 编码
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// 读取文本文件并生成快照：原始字节、解码文本、格式信息与指纹（存在性/长度/修改时间/SHA-256）。
    /// 文件不存在时返回 Missing 指纹的空快照。
    /// </summary>
    public static async Task<TextFileSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new TextFileSnapshot(path, [], string.Empty, new TextFileFormat(false, Environment.NewLine, false, false), FileFingerprint.Missing);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var content = hasBom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes.AsSpan();
        var text = StrictUtf8.GetString(content);
        var format = DetectFormat(text, hasBom);
        var fileInfo = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var fingerprint = new FileFingerprint(true, bytes.LongLength, fileInfo.LastWriteTimeUtc, hash);
        return new TextFileSnapshot(path, bytes, text, format, fingerprint);
    }

    /// <summary>按目标格式把文本编码为字节序列（按需补 UTF-8 BOM）。</summary>
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

    /// <summary>
    /// 统计文本中的 CRLF/LF 数量推断换行风格：CRLF 占多数则用 CRLF，否则用 LF；
    /// 两者皆无（无换行）时沿用系统换行符。同时记录是否以换行结尾、是否为混合换行。
    /// </summary>
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

        return new TextFileFormat(hasBom, newLine, text.EndsWith('\n'), crlf > 0 && lf > 0);
    }
}
