namespace CBox.Core;

/// <summary>
/// Argon2id 密钥派生，KDF ID = 2。
///
/// 相比 PBKDF2 的差别：PBKDF2 只吃 CPU，攻击者用显卡并行跑几十个实例，
/// 每秒能试上亿个口令。Argon2id 每个候选口令要占满 64 MiB 内存，
/// 显卡的显存塞不下几个实例，爆破速度会掉好几个数量级。
///
/// 参数选择（RFC 9106 第二推荐档位，按单线程调整）：
///   - 内存 m = 64 MiB（固定）
///   - 迭代 t = 文件头里的 KdfIterations，默认 3
///   - 并行度 p = 1（桌面工具不抢多核，结果可复现）
///
/// 为什么内存固定：文件头只有一个 KdfIterations 字段，存不下 m。
/// 64 MiB 是 OWASP 2024 的推荐值，对现代机器没有压力。
/// </summary>
public sealed class Argon2idKdf : IKdf
{
    /// <summary>默认内存：64 MiB（OWASP 2024 推荐）。</summary>
    public const int DefaultMemoryMiB = 64;

    /// <summary>
    /// 文件头里 KdfIterations 的编码方式：(内存 MiB &lt;&lt; 8) | 轮数。
    ///
    /// 这样只用一个 32 位字段就能同时记住两个参数。
    /// 旧文件里存的是裸轮数（小于 256），解密时按 64 MiB 解释，保证向后兼容。
    /// </summary>
    public static uint Encode(uint rounds, int memoryMiB) => ((uint)memoryMiB << 8) | (rounds & 0xFF);

    /// <summary>解码出 (轮数, 内存 MiB)。</summary>
    public static (uint Rounds, int MemoryMiB) Decode(uint stored)
    {
        // 小于 256 说明是旧格式：只有轮数，内存按默认值
        return stored < 256
            ? (stored, DefaultMemoryMiB)
            : (stored & 0xFF, (int)(stored >> 8));
    }

    /// <summary>并行度固定为 1。</summary>
    public const uint Lanes = 1;

    public byte KdfId => 2;

    public string DisplayName => "Argon2id";

    public uint DefaultIterations => 3;

    public byte[] Derive(byte[] password, byte[] salt, uint iterations, int keyLength)
    {
        var (rounds, memoryMiB) = Decode(iterations);

        if (rounds is < 1 or > 100)
        {
            throw new CbxException("Argon2id 的轮数应在 1 到 100 之间。");
        }

        if (memoryMiB is < 8 or > 1024)
        {
            throw new CbxException("Argon2id 的内存应在 8 到 1024 MiB 之间。");
        }

        return Argon2Core.Derive(
            password,
            salt,
            rounds,
            memoryMiB * 1024,
            keyLength,
            Lanes);
    }
}
