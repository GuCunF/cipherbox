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
    /// <summary>Argon2 内存块数：64 MiB。</summary>
    public const int MemoryKiB = 64 * 1024;

    /// <summary>并行度固定为 1。</summary>
    public const uint Lanes = 1;

    public byte KdfId => 2;

    public string DisplayName => "Argon2id";

    public uint DefaultIterations => 3;

    public byte[] Derive(byte[] password, byte[] salt, uint iterations, int keyLength)
    {
        if (iterations is < 1 or > 100)
        {
            throw new CbxException("Argon2id 的迭代次数应在 1 到 100 之间。");
        }

        return Argon2Core.Derive(
            password,
            salt,
            iterations,
            MemoryKiB,
            keyLength,
            Lanes);
    }
}
