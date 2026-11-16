using System.Buffers.Binary;
using System.Text;

namespace CBox.Core;

/// <summary>
/// 容器格式常量。格式的说明见 README.md，这里是唯一的实现来源。
///
/// 头布局（所有整数均为大端）：
///   magic[8] "CBXFILE1" | ver u8 | algId u8 | kdfId u8 | flags u8
///   chunkSize u32 | plainSize u64 | chunkCount u32 | kdfIterations u32
///   saltLen u8 | salt[16] | nonceLen u8 | baseNonce[12]
///   nameLen u16 | name[nameLen] (UTF-8)
/// 正文：
///   N 个块，每块 = ctLen u32 | ciphertext | tag[16]
/// </summary>
public static class CbxFormat
{
    public static ReadOnlySpan<byte> Magic => "CBXFILE1"u8;

    /// <summary>
    /// 当前写出的格式版本。
    /// v1 只支持单文件；v2 增加「批量成员」与「归档」两种 kind。
    /// 读取端同时接受 v1 与 v2。
    /// </summary>
    public const byte Version = 2;

    /// <summary>仍然能读的最老版本。</summary>
    public const byte MinReadableVersion = 1;

    /// <summary>kind 字段：这是什么类型的载体。</summary>
    public const byte KindSingle = 0;    // 单文件（v1 的语义，也是 v2 默认）
    public const byte KindBatch = 1;     // 批量加密的成员文件
    public const byte KindArchive = 2;   // 归档（多个文件打包进一个 .cbx）

    /// <summary>批次 ID 长度（随机数，标识"同一批加密"）。</summary>
    public const int BatchIdSize = 12;

    /// <summary>归档专用标志位（archiveFlags）：bit0 = 允许输口令后预览目录结构。</summary>
    public const byte ArchiveFlagPreviewable = 0x01;

    public const byte DefaultAlgId = 1;
    public const byte DefaultKdfId = 1;

    public const uint DefaultIterations = 600_000;

    // 加密强度档位（只改 PBKDF2 迭代次数，格式不变：迭代次数本来就在文件头里）
    public const uint QuickIterations = 100_000;
    public const uint StandardIterations = 600_000;

    // 实测这台机器约 1100 万次/秒：1000 万次 ≈ 0.95 秒，作为"高强度"有实际等待感
    public const uint StrongIterations = 10_000_000;
    public const int DefaultChunkSize = 1 << 20;   // 1 MiB
    public const int MinChunkSize = 4 << 10;       // 4 KiB
    public const int MaxChunkSize = 64 << 20;      // 64 MiB

    public const int SaltSize = 16;
    public const int FixedPartSize = 32;           // magic..kdfIterations

    /// <summary>头里带原始文件名。</summary>
    public const byte FlagHasName = 0x01;

    /// <summary>
    /// 该文件不做后缀名检查（由专业模式在加密时写入）。
    /// 位已经预留，旧版本读到未定义的位会忽略——所以这是向后兼容的，不需要升格式版本。
    /// </summary>
    public const byte FlagSkipTypeCheck = 0x02;

    /// <summary>头部记录了内容类别（下面两个位才有意义）。</summary>
    public const byte FlagHasCategory = 0x04;

    /// <summary>类别：00=文本 01=图片 10=其他。</summary>
    public const byte FlagCategoryImage = 0x08;
    public const byte FlagCategoryOther = 0x10;

    /// <summary>文件名以密文存放。旧版本读到会忽略这个位，只是显示不出名字。</summary>
    public const byte FlagEncryptedName = 0x20;

    /// <summary>头部带批次 ID（v2 起有效）。</summary>
    public const byte FlagHasBatch = 0x40;
}
