namespace CBox.Core;

/// <summary>
/// 一次加密所用的完整参数组合。
///
/// 为什么要有它：默认值原本散落在 CbxFormat、命令行参数解析、界面下拉框三处，
/// 将来做「一般模式 / 专业模式」时会出现"同一个默认值三份、改一处漏两处"。
/// 现在所有默认都从这里出发：
///   - 一般模式 → 直接用 <see cref="Standard"/>
///   - 专业模式 → 从预设出发再逐项改
///
/// 容器里记录的仍然是 algId / kdfId / iterations / chunkSize 这几个字段，
/// 所以这个类只是"怎么选参数"，不影响文件格式。
/// </summary>
public sealed class CbxProfile
{
    public required string Name { get; init; }
    public required byte AlgorithmId { get; init; }
    public required byte KdfId { get; init; }
    public required uint Iterations { get; init; }
    public required int ChunkSize { get; init; }

    /// <summary>载体类型：单文件 / 批量成员 / 归档。</summary>
    public byte Kind { get; init; } = CbxFormat.KindSingle;

    /// <summary>批次 ID（kind = 批量成员时使用，同批共享）。</summary>
    public byte[] BatchId { get; init; } = Array.Empty<byte>();

    /// <summary>归档专用标志（bit0 = 允许预览）。</summary>
    public byte ArchiveFlags { get; init; }

    /// <summary>
    /// 写进文件头的「不做后缀名检查」标记。
    /// 开启后，这个文件在任何机器、任何模式下都不再提示后缀不符——专业模式专用。
    /// </summary>
    public bool SkipTypeCheck { get; init; }

    /// <summary>
    /// 文件名是否加密。默认关闭——一般模式下名字照常可读，
    /// 只有专业模式勾了「全加密」才打开，那时连扩展名都看不到。
    /// </summary>
    public bool EncryptName { get; init; }

    /// <summary>
    /// 是否把类别也藏起来（全加密的一部分）。
    /// 做法是**写一个假的「其他」**，而不是留"已隐藏"标记——
    /// 标记本身就等于告诉对方"这里有东西藏着"，假值反而没有任何信息量。
    /// </summary>
    public bool HideCategory { get; init; }

    /// <summary>
    /// 是否把头部的类别位写进去（默认写）。
    /// 关掉可以写出与 1.3 及更早版本结构一致的文件，用于兼容性回归。
    /// </summary>
    public bool WriteCategory { get; init; } = true;

    /// <summary>
    /// 存进文件头的显示名。为空则用真实文件名。
    /// 作用是让"文件里记录的名字"和"磁盘上的名字"解耦——
    /// 例如加密 a.txt 但让对方看到「会议纪要.txt」。
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>快速：适合临时文件，抗离线爆破较弱。</summary>
    /// <summary>Argon2id 的迭代档位：轮数（内存固定 64 MiB）。</summary>
    public const uint Argon2QuickIterations = 1;
    public const uint Argon2StandardIterations = 3;
    public const uint Argon2StrongIterations = 6;

    /// <summary>
    /// 按指定 KDF 生成档位。PBKDF2 的 Iterations 是迭代次数，
    /// Argon2id 的 Iterations 是轮数——同一个字段，两种语义，
    /// 解密端靠文件头的 KdfId 区分。
    /// </summary>
    public static CbxProfile QuickWith(byte kdfId) => WithKdf(kdfId, "快速", CbxFormat.QuickIterations, Argon2QuickIterations);

    public static CbxProfile StandardWith(byte kdfId) => WithKdf(kdfId, "标准", CbxFormat.StandardIterations, Argon2StandardIterations);

    public static CbxProfile StrongWith(byte kdfId) => WithKdf(kdfId, "高强度", CbxFormat.StrongIterations, Argon2StrongIterations);

    private static CbxProfile WithKdf(byte kdfId, string name, uint pbkdf2Iterations, uint argon2Iterations) => new()
    {
        Name = name,
        AlgorithmId = CbxFormat.DefaultAlgId,
        KdfId = kdfId,
        Iterations = kdfId == 2 ? argon2Iterations : pbkdf2Iterations,
        ChunkSize = CbxFormat.DefaultChunkSize,
    };
    public static CbxProfile Quick => new()
    {
        Name = "快速",
        AlgorithmId = CbxFormat.DefaultAlgId,
        KdfId = CbxFormat.DefaultKdfId,
        Iterations = CbxFormat.QuickIterations,
        ChunkSize = CbxFormat.DefaultChunkSize,
    };

    /// <summary>标准：日常使用的默认档。</summary>
    public static CbxProfile Standard => new()
    {
        Name = "标准",
        AlgorithmId = CbxFormat.DefaultAlgId,
        KdfId = CbxFormat.DefaultKdfId,
        Iterations = CbxFormat.StandardIterations,
        ChunkSize = CbxFormat.DefaultChunkSize,
    };

    /// <summary>高强度：约 1 秒的派生时间，适合长期保存的敏感资料。</summary>
    public static CbxProfile Strong => new()
    {
        Name = "高强度",
        AlgorithmId = CbxFormat.DefaultAlgId,
        KdfId = CbxFormat.DefaultKdfId,
        Iterations = CbxFormat.StrongIterations,
        ChunkSize = CbxFormat.DefaultChunkSize,
    };

    /// <summary>程序的默认档位。一般模式、命令行不带参数时都用它。</summary>
    public static CbxProfile Default => Standard;

    public override string ToString()
    {
        return $"{Name}(alg={AlgorithmId}, kdf={KdfId}, iter={Iterations:N0}, chunk={ChunkSize:N0})";
    }
}
