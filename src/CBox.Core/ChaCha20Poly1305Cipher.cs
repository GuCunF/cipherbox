using System.Security.Cryptography;

namespace CBox.Core;

/// <summary>
/// ChaCha20-Poly1305，算法 ID = 2。
///
/// 与 AES-256-GCM 同为 AEAD（带认证标签），.NET 自带实现，零第三方依赖。
///
/// 为什么要有第二种算法：
///   - AES-GCM 在**没有 AES 硬件指令**的机器上较慢，ChaCha20 是纯软件实现且更快
///   - 是一种"换一种算法仍然能用"的验证：格式里本来就有 algId，加算法不该动格式
///
/// 参数与 AES-GCM 保持一致（32 字节密钥、12 字节 nonce、16 字节标签），
/// 所以分块逻辑、nonce 构造、AAD 构造全部复用，不需要任何特判。
/// </summary>
public sealed class ChaCha20Poly1305Cipher : ICipher
{
    public byte AlgorithmId => 2;
    public string DisplayName => "ChaCha20-Poly1305";
    public int KeySize => 32;
    public int NonceSize => 12;
    public int TagSize => 16;

    public byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var chacha = new ChaCha20Poly1305(key))
        {
            chacha.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        var result = new byte[ciphertext.Length + TagSize];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, TagSize);
        return result;
    }

    public byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[] aad)
    {
        if (ciphertextWithTag.Length < TagSize)
        {
            throw new CbxException("数据块长度异常，文件已损坏。");
        }

        int ctLength = ciphertextWithTag.Length - TagSize;
        var ciphertext = new byte[ctLength];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(ciphertextWithTag, 0, ciphertext, 0, ctLength);
        Buffer.BlockCopy(ciphertextWithTag, ctLength, tag, 0, TagSize);

        var plaintext = new byte[ctLength];
        try
        {
            using var chacha = new ChaCha20Poly1305(key);
            chacha.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            throw new CbxException("解密失败：口令错误，或者文件已被修改/损坏。", ex);
        }

        return plaintext;
    }
}
