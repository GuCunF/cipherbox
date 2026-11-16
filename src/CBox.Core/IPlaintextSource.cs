namespace CBox.Core;

/// <summary>
/// 明文数据源：只读、逐块提供解密后的内容。
///
/// 现在只有"直接读本地 .cbx"一种实现（<see cref="CbxDecryptReader"/>）。
/// 将来若要做"服务代理解密、客户端拿不到密钥"那种架构，
/// 只需再写一个实现，查看器一行都不用改。
/// </summary>
public interface IPlaintextSource : IDisposable
{
    /// <summary>文件头里记录的原始文件名。</summary>
    string? OriginalName { get; }

    /// <summary>明文总长度。</summary>
    long PlainSize { get; }

    /// <summary>每块明文大小上限。</summary>
    int ChunkSize { get; }

    /// <summary>总块数。</summary>
    int ChunkCount { get; }

    /// <summary>已经解出的明文字节数。</summary>
    long DeliveredBytes { get; }

    /// <summary>取出下一块明文；全部读完后返回 null。</summary>
    Task<byte[]?> ReadBlockAsync(CancellationToken cancellationToken = default);
}
