using System.Security.Cryptography;
using System.Text;

namespace CBox.Core;

/// <summary>
/// 文件名的加解密。
///
/// 名字被加密后，攻击者在拿到口令之前连"这是个什么文件"都不知道。
/// 实现上复用分块的 AEAD，只是挪用一个保留的块号，**不改动头部结构**。
///
/// 注意 AAD 不能直接用头部哈希——头部里就装着名字，会形成循环依赖。
/// 这里改用「盐 + nonce + 算法号 + 固定标签」推出 AAD，随文件随机变化。
/// </summary>
public static class CbxNameCodec
{
    /// <summary>保留的块号，绝不与真实数据块冲突。</summary>
    private const uint NameChunkIndex = 0xFFFFFFFF;

    public static byte[] BuildAad(CbxHeader header)
    {
        using var ms = new MemoryStream();
        ms.Write(header.Salt);
        ms.Write(header.BaseNonce);
        ms.WriteByte(header.AlgId);
        ms.WriteByte(header.KdfId);
        ms.Write("CBXNAME1"u8);
        return SHA256.HashData(ms.ToArray());
    }

    public static byte[] Encrypt(ICipher cipher, CbxHeader header, byte[] key, string name)
    {
        byte[] plain = Encoding.UTF8.GetBytes(name);
        byte[] nonce = CbxChunking.BuildNonce(header.BaseNonce, NameChunkIndex);
        return cipher.Encrypt(key, nonce, plain, BuildAad(header));
    }

    /// <summary>解不出来就返回 null——名字坏了不该让整个文件打不开。</summary>
    public static string? TryDecrypt(ICipher cipher, CbxHeader header, byte[] key, byte[] ciphertext)
    {
        try
        {
            byte[] nonce = CbxChunking.BuildNonce(header.BaseNonce, NameChunkIndex);
            byte[] plain = cipher.Decrypt(key, nonce, ciphertext, BuildAad(header));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
