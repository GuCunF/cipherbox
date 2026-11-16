namespace CBox.Core;

/// <summary>
/// 公钥封装（KEM）接口。**当前没有任何实现**——这是为 1.6 / 下个大版本预留的位置。
///
/// 用法设想（格式 v2）：
///   1. 发送方用接收方的公钥做一次 <see cref="Encapsulate"/>，得到密文包和共享密钥
///   2. 共享密钥经 KDF 派生后，用来包裹本次随机生成的内容密钥（CEK）
///   3. 文件头里存「KEM 编号 + 公钥密文 + 被包裹的 CEK」
///   4. 接收方用私钥 <see cref="Decapsulate"/> 还原共享密钥，再解出 CEK
///
/// 为什么现在就定义：等真要加的时候，只需实现一个类 + 注册一行，
/// 加密编排、读取器、界面都不用动——和各种算法注册表的思路一致。
///
/// 编号规划（写进文件头，永不复用）：
///   1 = X25519 + HKDF-SHA256（预留，最可能先做）
///   2 = 预留
/// </summary>
public interface IKem
{
    /// <summary>写进文件头的编号。0 保留给"没有公钥封装"。</summary>
    byte Id { get; }

    string DisplayName { get; }

    /// <summary>公钥长度（字节）。用来校验界面里粘贴的公钥是否合理。</summary>
    int PublicKeySize { get; }

    /// <summary>私钥长度（字节）。</summary>
    int PrivateKeySize { get; }

    /// <summary>发送方：用接收方公钥封装，返回密钥密文与共享密钥。</summary>
    (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(byte[] publicKey);

    /// <summary>接收方：用私钥解开密钥密文，得到同一个共享密钥。</summary>
    byte[] Decapsulate(byte[] ciphertext, byte[] privateKey);
}
