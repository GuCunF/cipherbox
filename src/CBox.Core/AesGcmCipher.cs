using System.Security.Cryptography;

namespace CBox.Core;

/// <summary>
/// AES-256-GCM，算法 ID = 1。
/// .NET 自带实现，无需任何第三方库。
/// </summary>
public sealed class AesGcmCipher : ICipher
{
    public byte AlgorithmId => 1;
    public string DisplayName => "AES-256-GCM";
    public int KeySize => 32;
    public int NonceSize => 12;
    public int TagSize => 16;

    public byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
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
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            throw new CbxException("解密失败：口令错误，或者文件已被修改/损坏。", ex);
        }

        return plaintext;
    }
}
