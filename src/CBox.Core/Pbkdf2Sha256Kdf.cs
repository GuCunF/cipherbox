using System.Security.Cryptography;

namespace CBox.Core;

/// <summary>
/// PBKDF2-HMAC-SHA256，KDF ID = 1。
/// 选它是因为 .NET 自带、无需第三方包；后续可加 Argon2id / scrypt 作为更高档位。
/// </summary>
public sealed class Pbkdf2Sha256Kdf : IKdf
{
    public byte KdfId => 1;
    public string DisplayName => "PBKDF2-HMAC-SHA256";
    public uint DefaultIterations => 600_000;

    public byte[] Derive(byte[] password, byte[] salt, uint iterations, int keyLength)
    {
        if (iterations < 10_000)
        {
            throw new CbxException("迭代次数过低（至少 10000），拒绝使用弱参数。");
        }

        // 走缓存：批量解密同一批文件时，盐和迭代次数相同就不用重复跑 60 万次
        return DerivedKeyCache.Derive(password, salt, iterations, keyLength);
    }
}