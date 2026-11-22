using System.Security.Cryptography;

namespace CBox.Core;

/// <summary>
/// 密钥文件（口令之外的第二把钥匙）。
///
/// 原理：把口令和密钥文件的哈希**拼在一起**再交给 KDF。
///   combined = password || SHA256(keyFileData)[0..31]
///   key      = KDF(combined, salt, ...)
///
/// 这样一来：
///   - 只知道口令做不到，还得有那个文件；
///   - 只知道文件也做不到，还得有口令；
///   - 文件里**不存**任何能反推密钥文件内容的东西，只存一个「需要密钥文件」的标志位。
///
/// 代价：密钥文件一旦丢失，文件就永久解不开。界面上会明确警告。
/// </summary>
public static class KeyFile
{
    /// <summary>取密钥文件内容派生的 32 字节指纹。</summary>
    public static byte[] Fingerprint(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// 把口令和密钥文件指纹拼成实际交给 KDF 的口令。
    /// 没有密钥文件时直接返回口令本身（保持与旧文件完全兼容）。
    /// </summary>
    public static byte[] Combine(byte[] password, byte[]? keyFileFingerprint)
    {
        if (keyFileFingerprint is null || keyFileFingerprint.Length == 0)
        {
            return password;
        }

        var combined = new byte[password.Length + keyFileFingerprint.Length];
        password.CopyTo(combined, 0);
        keyFileFingerprint.CopyTo(combined, password.Length);

        return combined;
    }

    /// <summary>
    /// 从文件路径读内容并算指纹。
    ///
    /// 空文件直接拒绝：任何 0 字节文件算出来的指纹都一样，
    /// 等于「随便找个空文件就能开门」，第二把钥匙形同虚设。
    /// </summary>
    public static byte[] FingerprintFromFile(string path)
    {
        var info = new FileInfo(path);

        if (!info.Exists)
        {
            throw new CbxException($"找不到密钥文件：{path}");
        }

        if (info.Length == 0)
        {
            throw new CbxException(
                $"密钥文件是空的（0 字节）：{path}\n\n" +
                "空文件不能当钥匙——所有空文件算出来的内容都一样，" +
                "等于随便找个空文件就能开门，第二重保护就失效了。\n" +
                "请换一个确实有内容的文件（一张图片、一个文档都行）。");
        }

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);

        return Fingerprint(stream);
    }

    private static byte[] Fingerprint(Stream stream)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }
}
