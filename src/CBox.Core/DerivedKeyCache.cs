using System.Security.Cryptography;

namespace CBox.Core;

/// <summary>
/// 密钥推导结果的小缓存。
///
/// 为什么需要：批量解密一个文件夹时，每个文件都要重新跑一遍 PBKDF2（标准档 60 万次，
/// 约 0.13 秒）。一个 100 个文件的文件夹就要多花 20 多秒，而这些文件的
/// 盐值和迭代次数通常是相同的（同一批加密出来的）。
///
/// 做法：用「口令的哈希 + 盐 + 迭代次数 + 长度」做键，命中就复用。
///
/// **进出都必须是副本**：调用方（加密的 finally、解密器的 Dispose）用完会把密钥
/// 涂零。如果这里直接返回缓存里的那个数组，人家一涂缓存就变全零，下一个文件
/// 用全零密钥去解密必然失败——「第一个文件成功、后面全失败」就是这个坑。
/// </summary>
public static class DerivedKeyCache
{
    private const int MaxEntries = 8;

    private static readonly Dictionary<string, byte[]> Entries = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>是否启用缓存。默认开启；测试里可以关掉以观察真实耗时。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>当前缓存条目数（诊断用）。</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }

    /// <summary>
    /// 取密钥：命中就复用，否则真的跑一遍 PBKDF2。
    /// 返回值永远是调用方独占的副本，可以随意涂零。
    /// </summary>
    internal static byte[] Derive(byte[] password, byte[] salt, uint iterations, int keyLength)
    {
        if (!Enabled)
        {
            return Pbkdf2(password, salt, iterations, keyLength);
        }

        string key = BuildKey(password, salt, iterations, keyLength);

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out byte[]? hit))
            {
                return (byte[])hit.Clone();
            }
        }

        byte[] derived = Pbkdf2(password, salt, iterations, keyLength);

        lock (Gate)
        {
            if (Entries.Count >= MaxEntries)
            {
                // 先涂零再丢，别让整批旧密钥在内存里等着被 GC
                foreach (byte[] value in Entries.Values)
                {
                    Array.Clear(value);
                }

                Entries.Clear();
            }

            Entries[key] = (byte[])derived.Clone();
        }

        return derived;
    }

    /// <summary>清空缓存。批量操作结束、切换文件、程序退出前都可以调。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            // 先把内容涂掉再丢掉引用，减少它在内存里停留的时间
            foreach (byte[] value in Entries.Values)
            {
                Array.Clear(value);
            }

            Entries.Clear();
        }
    }

    private static byte[] Pbkdf2(byte[] password, byte[] salt, uint iterations, int keyLength)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password, salt, (int)iterations, HashAlgorithmName.SHA256, keyLength);
    }

    /// <summary>
    /// 缓存键：口令不能直接当键（会被拼进字符串长期驻留），所以用它的哈希。
    /// 盐和迭代次数一起进去——不同参数派生的结果绝不能混用。
    /// </summary>
    private static string BuildKey(byte[] password, byte[] salt, uint iterations, int keyLength)
    {
        byte[] passwordHash = SHA256.HashData(password);
        return $"{Convert.ToHexString(passwordHash)}:{Convert.ToHexString(salt)}:{iterations}:{keyLength}";
    }
}
