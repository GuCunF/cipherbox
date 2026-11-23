namespace CBox.Core;

/// <summary>
/// 一种"对称加密算法"的抽象。新增算法 = 实现本接口 + 在注册表里注册一行。
/// 实现必须是 AEAD（带认证标签），否则容器格式的完整性保证不成立。
/// </summary>
public interface ICipher
{
    /// <summary>写进文件头的算法编号，发布后永不复用。</summary>
    byte AlgorithmId { get; }

    /// <summary>给人看的名字，出现在加密端的选项列表里。</summary>
    string DisplayName { get; }

    /// <summary>密钥长度（字节）。</summary>
    int KeySize { get; }

    /// <summary>单次加密所需 nonce 长度（字节）。</summary>
    int NonceSize { get; }

    /// <summary>认证标签长度（字节）。</summary>
    int TagSize { get; }

    /// <summary>加密一个块，返回 ciphertext || tag。</summary>
    byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad);

    /// <summary>解密一个块，输入为 ciphertext || tag，校验失败必须抛异常。</summary>
    byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[] aad);
}

/// <summary>
/// 一种"口令到密钥"的派生函数。新增 KDF 同样是实现接口 + 注册。
/// </summary>
public interface IKdf
{
    /// <summary>写进文件头的 KDF 编号，发布后永不复用。</summary>
    byte KdfId { get; }

    /// <summary>给人看的名字。</summary>
    string DisplayName { get; }

    /// <summary>默认迭代/工作因子。</summary>
    uint DefaultIterations { get; }

    /// <summary>用口令 + 盐派生出指定长度的密钥。</summary>
    byte[] Derive(byte[] password, byte[] salt, uint iterations, int keyLength);
}

/// <summary>加密算法注册表。解密端靠文件头里的 AlgorithmId 自动找回实现。</summary>
public static class CipherRegistry
{
    private static readonly Dictionary<byte, ICipher> Map = new();

    static CipherRegistry()
    {
        Register(new AesGcmCipher());
        Register(new ChaCha20Poly1305Cipher());
    }

    public static void Register(ICipher cipher)
    {
        Map[cipher.AlgorithmId] = cipher;
    }

    public static ICipher Get(byte algorithmId)
    {
        return Map.TryGetValue(algorithmId, out var cipher)
            ? cipher
            : throw new CbxException(CbxErrorCode.UnsupportedAlgorithm, algorithmId);
    }

    public static bool IsSupported(byte algorithmId) => Map.ContainsKey(algorithmId);

    public static IEnumerable<ICipher> All => Map.Values.OrderBy(x => x.AlgorithmId);
}

/// <summary>口令派生函数注册表。</summary>
public static class KdfRegistry
{
    private static readonly Dictionary<byte, IKdf> Map = new();

    static KdfRegistry()
    {
        Register(new Pbkdf2Sha256Kdf());
        Register(new Argon2idKdf());
    }

    public static void Register(IKdf kdf)
    {
        Map[kdf.KdfId] = kdf;
    }

    public static IKdf Get(byte kdfId)
    {
        return Map.TryGetValue(kdfId, out var kdf)
            ? kdf
            : throw new CbxException(CbxErrorCode.UnsupportedKdf, kdfId);
    }

    public static IEnumerable<IKdf> All => Map.Values.OrderBy(x => x.KdfId);
}
