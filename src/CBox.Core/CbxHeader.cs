using System.Buffers.Binary;
using System.Text;

namespace CBox.Core;

/// <summary>
/// 容器头。解析时保留原始字节（RawBytes），因为 AAD 用它的 SHA-256，
/// 这样即使头字段被改一个比特，所有数据块都会校验失败。
/// </summary>
public sealed class CbxHeader
{
    public byte Version { get; set; }
    public byte AlgId { get; set; }
    public byte KdfId { get; set; }
    public byte Flags { get; set; }
    public uint ChunkSize { get; set; }
    public ulong PlainSize { get; set; }
    public uint ChunkCount { get; set; }
    public uint KdfIterations { get; set; }
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] BaseNonce { get; set; } = Array.Empty<byte>();
    /// <summary>载体类型：0=单文件 1=批量成员 2=归档（v1 文件一律视为 0）。</summary>
    public byte Kind { get; set; } = CbxFormat.KindSingle;

    /// <summary>批次 ID：同一批加密的成员共享，v1 文件为空。</summary>
    public byte[] BatchId { get; set; } = Array.Empty<byte>();

    /// <summary>归档专用标志（bit0 = 允许预览）。</summary>
    public byte ArchiveFlags { get; set; }

    public bool HasBatch => (Flags & CbxFormat.FlagHasBatch) != 0 && BatchId.Length > 0;

    public bool IsBatchMember => Kind == CbxFormat.KindBatch;

    public bool IsArchive => Kind == CbxFormat.KindArchive;

    public bool IsPreviewable => IsArchive && (ArchiveFlags & CbxFormat.ArchiveFlagPreviewable) != 0;

    /// <summary>文件名在文件里的原始字节：可能是明文，也可能是密文。</summary>
    public byte[] NameBytes { get; set; } = Array.Empty<byte>();

    /// <summary>明文文件名。名字加密时由读取器在拿到密钥后填入。</summary>
    public string? OriginalName { get; set; }

    /// <summary>从文件读上来的原始头字节；序列化时生成的头则为空。</summary>
    public byte[] RawBytes { get; set; } = Array.Empty<byte>();

    public bool HasName => (Flags & CbxFormat.FlagHasName) != 0 && NameBytes.Length > 0;

    /// <summary>文件名是否以密文存放。</summary>
    public bool EncryptedName => (Flags & CbxFormat.FlagEncryptedName) != 0;

    /// <summary>该文件是否声明「不做后缀名检查」（加密时由专业模式写入）。</summary>
    public bool SkipTypeCheck => (Flags & CbxFormat.FlagSkipTypeCheck) != 0;

    /// <summary>头部是否记录了内容类别。</summary>
    public bool HasCategory => (Flags & CbxFormat.FlagHasCategory) != 0;

    /// <summary>
    /// 内容类别：0=文本、1=图片、2=其他；未记录返回 -1。
    /// 它的用途是"输口令之前就能判断能不能预览"，所以必须存在头部而不是名字里。
    /// </summary>
    public int CategoryCode
    {
        get
        {
            if (!HasCategory)
            {
                return -1;
            }

            if ((Flags & CbxFormat.FlagCategoryImage) != 0)
            {
                return 1;
            }

            return (Flags & CbxFormat.FlagCategoryOther) != 0 ? 2 : 0;
        }
    }

    public string CategoryName => CategoryCode switch
    {
        0 => "文本",
        1 => "图片",
        2 => "其他",
        _ => "未记录",
    };

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.Write(CbxFormat.Magic);
        ms.WriteByte(Version);
        ms.WriteByte(AlgId);
        ms.WriteByte(KdfId);
        ms.WriteByte(Flags);

        Span<byte> u32 = stackalloc byte[4];
        Span<byte> u64 = stackalloc byte[8];
        Span<byte> u16 = stackalloc byte[2];

        BinaryPrimitives.WriteUInt32BigEndian(u32, ChunkSize);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt64BigEndian(u64, PlainSize);
        ms.Write(u64);
        BinaryPrimitives.WriteUInt32BigEndian(u32, ChunkCount);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32BigEndian(u32, KdfIterations);
        ms.Write(u32);

        ms.WriteByte((byte)Salt.Length);
        ms.Write(Salt);
        ms.WriteByte((byte)BaseNonce.Length);
        ms.Write(BaseNonce);

        // 名字可能是密文，所以存原始字节；明文名另外放在 OriginalName 里
        var nameBytes = NameBytes.Length > 0
            ? NameBytes
            : (string.IsNullOrEmpty(OriginalName) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(OriginalName));

        // 不截断：对密文截断会破坏认证标签，名字将永久解不开。
        // 宁可在这里明确失败，也不要写出一个"名字坏了"的文件。
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new CbxException(
                $"文件名过长（{nameBytes.Length:N0} 字节），超出文件头字段上限 {ushort.MaxValue:N0} 字节。");
        }

        BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)nameBytes.Length);
        ms.Write(u16);
        ms.Write(nameBytes);

        // v2 追加：kind + 批次 ID + 归档标志
        ms.WriteByte(Kind);

        if ((Flags & CbxFormat.FlagHasBatch) != 0 && BatchId.Length > 0)
        {
            ms.WriteByte((byte)BatchId.Length);
            ms.Write(BatchId);
        }
        else
        {
            ms.WriteByte(0);
        }

        ms.WriteByte(ArchiveFlags);

        return ms.ToArray();
    }

    public static CbxHeader Read(Stream stream)
    {
        using var collected = new MemoryStream();

        var fixedPart = ReadExactly(stream, CbxFormat.FixedPartSize);
        collected.Write(fixedPart);

        var span = fixedPart.AsSpan();
        if (!span[..8].SequenceEqual(CbxFormat.Magic))
        {
            throw new CbxException("这不是本程序的加密文件（文件头标识不匹配）。");
        }

        var header = new CbxHeader
        {
            Version = span[8],
            AlgId = span[9],
            KdfId = span[10],
            Flags = span[11],
            ChunkSize = BinaryPrimitives.ReadUInt32BigEndian(span[12..]),
            PlainSize = BinaryPrimitives.ReadUInt64BigEndian(span[16..]),
            ChunkCount = BinaryPrimitives.ReadUInt32BigEndian(span[24..]),
            KdfIterations = BinaryPrimitives.ReadUInt32BigEndian(span[28..]),
        };

        if (header.Version < CbxFormat.MinReadableVersion || header.Version > CbxFormat.Version)
        {
            throw new CbxException($"不支持的文件版本（{header.Version}），请升级程序。");
        }

        int saltLen = ReadExactly(stream, 1)[0];
        collected.WriteByte((byte)saltLen);
        header.Salt = ReadExactly(stream, saltLen);
        collected.Write(header.Salt);

        int nonceLen = ReadExactly(stream, 1)[0];
        collected.WriteByte((byte)nonceLen);
        header.BaseNonce = ReadExactly(stream, nonceLen);
        collected.Write(header.BaseNonce);

        var nameLenBytes = ReadExactly(stream, 2);
        collected.Write(nameLenBytes);
        int nameLen = BinaryPrimitives.ReadUInt16BigEndian(nameLenBytes);

        if (nameLen > 0)
        {
            var nameBytes = ReadExactly(stream, nameLen);
            collected.Write(nameBytes);
            header.NameBytes = nameBytes;

            // 没加密的名字可以直接解出来；加密的要等读取器拿到密钥后再解
            if (!header.EncryptedName)
            {
                header.OriginalName = Encoding.UTF8.GetString(nameBytes);
            }
        }

        // v2 追加字段：kind + 批次 ID + 归档标志。v1 文件读到的是流的末尾，保持默认值。
        if (header.Version >= 2)
        {
            var kindByte = ReadExactly(stream, 1);
            collected.Write(kindByte);
            header.Kind = kindByte[0];

            var batchLenByte = ReadExactly(stream, 1);
            collected.Write(batchLenByte);
            int batchLen = batchLenByte[0];

            if (batchLen > 0)
            {
                var batchId = ReadExactly(stream, batchLen);
                collected.Write(batchId);
                header.BatchId = batchId;
            }

            var archiveFlagsByte = ReadExactly(stream, 1);
            collected.Write(archiveFlagsByte);
            header.ArchiveFlags = archiveFlagsByte[0];
        }

        header.RawBytes = collected.ToArray();
        return header;
    }

    /// <summary>把头里的参数和算法实现对照一遍，任何不自洽都直接拒绝。</summary>
    public void Validate(ICipher cipher)
    {
        if (ChunkSize < CbxFormat.MinChunkSize || ChunkSize > CbxFormat.MaxChunkSize)
        {
            throw new CbxException($"文件头里的块大小非法（{ChunkSize}）。");
        }

        if (Salt.Length != CbxFormat.SaltSize)
        {
            throw new CbxException($"文件头里的盐长度非法（{Salt.Length}）。");
        }

        if (BaseNonce.Length != cipher.NonceSize)
        {
            throw new CbxException($"文件头里的 nonce 长度与算法不符（{BaseNonce.Length}）。");
        }

        ulong expected = PlainSize == 0
            ? 0UL
            : (PlainSize + ChunkSize - 1) / ChunkSize;

        if (ChunkCount != expected)
        {
            throw new CbxException("文件头内部不自洽（块数量与明文大小不匹配）。");
        }
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"文件版本    : {Version}");
        sb.AppendLine($"加密算法    : {CipherRegistry.Get(AlgId).DisplayName} (ID={AlgId})");
        sb.AppendLine($"密钥派生    : {KdfRegistry.Get(KdfId).DisplayName} (ID={KdfId})");
        sb.AppendLine($"KDF 迭代    : {KdfIterations:N0}");
        sb.AppendLine($"原始文件名  : {(HasName ? (OriginalName ?? "（已加密，需口令）") : "(未记录)")}");
        sb.AppendLine($"明文大小    : {PlainSize:N0} 字节");
        sb.AppendLine($"块大小      : {ChunkSize:N0} 字节");
        sb.AppendLine($"块数量      : {ChunkCount:N0}");

        if (HasCategory)
        {
            sb.AppendLine($"内容类别    : {CategoryName}（{CategoryCode}）");
        }

        if (SkipTypeCheck)
        {
            sb.AppendLine("后缀检查    : 已在文件内标记跳过");
        }

        return sb.ToString().TrimEnd();
    }

    public static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        try
        {
            stream.ReadExactly(buffer, 0, count);
        }
        catch (EndOfStreamException ex)
        {
            throw new CbxException("文件不完整（提前结束）。", ex);
        }
        return buffer;
    }
}
