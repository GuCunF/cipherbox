using System.Buffers.Binary;

namespace CBox.Core;

/// <summary>
/// 分块的 nonce 与 AAD 推导规则。加密端和流式解密端必须用同一套，所以集中在这里。
/// 规则说明见 logs/02-格式规格.md。
/// </summary>
internal static class CbxChunking
{
    /// <summary>每块的 nonce：前面固定，最后 4 字节换成块序号，保证同一文件内绝不重复。</summary>
    public static byte[] BuildNonce(byte[] baseNonce, uint index)
    {
        var nonce = (byte[])baseNonce.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(nonce.Length - 4), index);
        return nonce;
    }

    /// <summary>
    /// 每块的 AAD = SHA256(头) || 块序号。
    /// 头被改 → 全部失败；块被重排 → 序号对不上；文件被截断 → 块数不足。
    /// </summary>
    public static byte[] BuildAad(byte[] aadBase, uint index)
    {
        var aad = new byte[aadBase.Length + 4];
        Buffer.BlockCopy(aadBase, 0, aad, 0, aadBase.Length);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(aadBase.Length), index);
        return aad;
    }
}
