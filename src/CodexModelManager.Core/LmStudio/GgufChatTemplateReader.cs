using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Core.Abstractions;
using CodexModelManager.Core.Models;

namespace CodexModelManager.Core.LmStudio;

public sealed class GgufChatTemplateReader : IGgufChatTemplateReader
{
    private const ulong MaximumMetadataCount = 1_000_000;
    private const ulong MaximumArrayElements = 20_000_000;
    private const ulong MaximumKeyBytes = 4096;
    private const ulong MaximumCapturedStringBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<GgufChatTemplateAnalysis> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        return Task.Run(() => ReadCore(fullPath, cancellationToken), cancellationToken);
    }

    private static GgufChatTemplateAnalysis ReadCore(string filePath, CancellationToken cancellationToken)
    {
        var before = new FileInfo(filePath);
        if (!before.Exists)
        {
            throw new FileNotFoundException("所选 GGUF 文件不存在。", filePath);
        }

        long expectedLength = before.Length;
        DateTime expectedLastWriteUtc = before.LastWriteTimeUtc;
        string? template = null;
        string? modelName = null;
        string? architecture = null;
        uint version;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
        using (var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: false))
        {
            Span<byte> magic = stackalloc byte[4];
            ReadExactly(reader, magic);
            if (!magic.SequenceEqual("GGUF"u8))
            {
                throw new InvalidDataException("文件不是 GGUF：magic 不匹配。");
            }

            version = reader.ReadUInt32();
            if (version is not (2 or 3))
            {
                throw new InvalidDataException($"不支持的 GGUF 版本 {version}；当前只读取 v2/v3 metadata。");
            }

            _ = reader.ReadUInt64(); // tensor count; model tensors are never read.
            ulong metadataCount = reader.ReadUInt64();
            if (metadataCount > MaximumMetadataCount)
            {
                throw new InvalidDataException("GGUF metadata 数量超过安全限制。");
            }

            bool templateSeen = false;
            for (ulong index = 0; index < metadataCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = ReadString(reader, MaximumKeyBytes, capture: true);
                uint type = reader.ReadUInt32();
                bool capture = key is "tokenizer.chat_template" or "general.name" or "general.architecture";
                if (key == "tokenizer.chat_template")
                {
                    if (templateSeen)
                    {
                        throw new InvalidDataException("GGUF 含重复 tokenizer.chat_template metadata。");
                    }

                    templateSeen = true;
                    if (type != 8)
                    {
                        throw new InvalidDataException("tokenizer.chat_template 不是单一 string；为避免猜测，拒绝修补。");
                    }
                }

                string? value = ReadOrSkipValue(reader, type, capture, depth: 0, cancellationToken);
                if (key == "tokenizer.chat_template") template = value;
                else if (key == "general.name") modelName = value;
                else if (key == "general.architecture") architecture = value;
            }
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidDataException("GGUF 未包含可用的 tokenizer.chat_template string。");
        }

        var after = new FileInfo(filePath);
        if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != expectedLastWriteUtc)
        {
            throw new IOException("GGUF 在读取 metadata 期间发生变化，请重新选择并分析。");
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(template)));
        return new GgufChatTemplateAnalysis(
            filePath,
            Path.GetFileName(filePath),
            expectedLength,
            new DateTimeOffset(expectedLastWriteUtc),
            version,
            modelName,
            architecture,
            template,
            sha256);
    }

    private static string? ReadOrSkipValue(
        BinaryReader reader,
        uint type,
        bool capture,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 4)
        {
            throw new InvalidDataException("GGUF metadata array 嵌套超过安全限制。");
        }

        int scalarSize = type switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0,
        };
        if (scalarSize != 0)
        {
            SkipBytes(reader, checked((ulong)scalarSize));
            return null;
        }

        if (type == 8)
        {
            return ReadString(reader, MaximumCapturedStringBytes, capture);
        }

        if (type != 9)
        {
            throw new InvalidDataException($"GGUF metadata 使用未知 value type {type}。");
        }

        uint elementType = reader.ReadUInt32();
        ulong count = reader.ReadUInt64();
        if (count > MaximumArrayElements)
        {
            throw new InvalidDataException("GGUF metadata array 超过安全元素限制。");
        }

        int elementSize = elementType switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0,
        };
        if (elementSize != 0)
        {
            ulong bytes = checked(count * (ulong)elementSize);
            SkipBytes(reader, bytes);
            return null;
        }

        for (ulong index = 0; index < count; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            _ = ReadOrSkipValue(reader, elementType, capture: false, depth + 1, cancellationToken);
        }

        return null;
    }

    private static string ReadString(BinaryReader reader, ulong maximumCapturedBytes, bool capture)
    {
        ulong byteCount = reader.ReadUInt64();
        if (capture && byteCount > maximumCapturedBytes)
        {
            throw new InvalidDataException("GGUF metadata string 超过安全读取限制。");
        }

        if (!capture)
        {
            SkipBytes(reader, byteCount);
            return string.Empty;
        }

        if (byteCount > int.MaxValue)
        {
            throw new InvalidDataException("GGUF metadata string 长度无法安全表示。");
        }

        byte[] bytes = reader.ReadBytes(checked((int)byteCount));
        if ((ulong)bytes.Length != byteCount)
        {
            throw new EndOfStreamException("GGUF metadata string 被截断。");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("GGUF metadata string 不是有效 UTF-8。", exception);
        }
    }

    private static void SkipBytes(BinaryReader reader, ulong byteCount)
    {
        Stream stream = reader.BaseStream;
        if (byteCount > long.MaxValue || stream.Position > stream.Length - checked((long)byteCount))
        {
            throw new EndOfStreamException("GGUF metadata 被截断。");
        }

        stream.Seek(checked((long)byteCount), SeekOrigin.Current);
    }

    private static void ReadExactly(BinaryReader reader, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = reader.Read(destination[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException("GGUF header 被截断。");
            }

            total += read;
        }
    }
}
