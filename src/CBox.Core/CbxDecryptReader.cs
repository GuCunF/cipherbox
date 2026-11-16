using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CBox.Core;

/// <summary>
/// 流式解密器：逐块解出明文，可以边解边用，不必先落成完整文件。
///
/// 用途：只读查看器解一块、显示一块，明文全程不写盘。
/// 典型用法：
/// <code>
/// using var reader = Cbx.OpenReader(path, password);
/// Console.WriteLine(reader.OriginalName);
/// while (reader.ReadBlock() is { } block)
/// {
///     // 处理这一块明文
/// }
/// </code>
/// 最后一块读完后会自动做完整性校验（块数、总长度、尾部多余数据）；
/// 任何篡改都会抛 <see cref="CbxException"/>。
/// </summary>
public sealed class CbxDecryptReader : IPlaintextSource
{
    private readonly FileStream _source;
    private readonly ICipher _cipher;
    private readonly byte[] _key;
    private readonly byte[] _aadBase;
    private readonly byte[] _lengthBuffer = new byte[4];

    private bool _finished;
    private bool _disposed;

    internal CbxDecryptReader(FileStream source, CbxHeader header, ICipher cipher, byte[] key)
    {
        _source = source;
        Header = header;
        _cipher = cipher;
        _key = key;
        _aadBase = SHA256.HashData(header.RawBytes);

        // 名字加密过的话，这里已经有密钥了，就地解开——
        // 之后所有调用方读 OriginalName 拿到的都是**明文名**，不需要各自处理。
        if (header.HasName && header.EncryptedName)
        {
            header.OriginalName = CbxNameCodec.TryDecrypt(cipher, header, key, header.NameBytes);
        }
    }

    /// <summary>文件头（已解析并校验过）。</summary>
    public CbxHeader Header { get; }

    /// <summary>文件头里记录的原始文件名；没有则为 null。</summary>
    public string? OriginalName => Header.HasName ? Header.OriginalName : null;

    /// <summary>明文总长度。</summary>
    public long PlainSize => (long)Header.PlainSize;

    /// <summary>每块明文大小上限。</summary>
    public int ChunkSize => (int)Header.ChunkSize;

    /// <summary>总块数。</summary>
    public int ChunkCount => (int)Header.ChunkCount;

    /// <summary>已经解出的明文字节数。</summary>
    public long DeliveredBytes { get; private set; }

    /// <summary>下一个要解出的块序号（等于已解出的块数）。</summary>
    public int NextChunkIndex { get; private set; }

    /// <summary>
    /// 解出下一块明文；全部读完后返回 null。
    /// 返回的数组长度 = 该块的明文长度（最后一块可能小于 ChunkSize）。
    /// </summary>
    public byte[]? ReadBlock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (NextChunkIndex >= ChunkCount)
        {
            EnsureComplete();
            return null;
        }

        _source.ReadExactly(_lengthBuffer, 0, 4);
        uint cipherLength = BinaryPrimitives.ReadUInt32BigEndian(_lengthBuffer);

        if (cipherLength < (uint)_cipher.TagSize ||
            cipherLength > (uint)ChunkSize + (uint)_cipher.TagSize)
        {
            throw new CbxException($"第 {NextChunkIndex + 1} 块长度非法，文件已损坏。");
        }

        // 钩子放在解密之前：小文件也要能从 0% 就开始停顿/失败
        CbxTestHooks.Throttle(NextChunkIndex, ChunkCount);
        CbxTestHooks.MaybeFailDecrypt(NextChunkIndex, ChunkCount);

        var block = CbxHeader.ReadExactly(_source, (int)cipherLength);
        var nonce = CbxChunking.BuildNonce(Header.BaseNonce, (uint)NextChunkIndex);
        var aad = CbxChunking.BuildAad(_aadBase, (uint)NextChunkIndex);

        byte[] plain;
        try
        {
            plain = _cipher.Decrypt(_key, nonce, block, aad);
        }
        catch (CbxException ex)
        {
            // 带上块号，界面才能说清是"第几块"出的问题
            throw new CbxException(ex.Message, NextChunkIndex, ex);
        }
        catch (Exception ex)
        {
            throw new CbxException("数据块认证失败（口令不对，或文件被改动过）。", NextChunkIndex, ex);
        }

        DeliveredBytes += plain.Length;
        NextChunkIndex++;

        return plain;
    }

    /// <summary>
    /// 异步取下一块。
    ///
    /// 说明：内部用线程池执行——解密本身是 CPU 计算，真正的异步磁盘 IO 收益有限，
    /// 真正的目的是让界面在解密期间保持响应。
    /// 同一个 reader 不允许并发调用，调用方要自己保证串行。
    /// </summary>
    public Task<byte[]?> ReadBlockAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReadBlock();
            },
            cancellationToken);
    }

    /// <summary>把剩余明文全部解出并写入目标流（解密端落盘时用）。</summary>
    public void CopyTo(Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (ReadBlock() is { } block)
        {
            output.Write(block, 0, block.Length);
        }
    }

    /// <summary>读完最后一块后的整体校验。</summary>
    private void EnsureComplete()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        if (DeliveredBytes != PlainSize)
        {
            throw new CbxException("解密结果长度与文件头不符，文件已被截断或篡改。");
        }

        if (_source.ReadByte() != -1)
        {
            throw new CbxException("文件末尾存在多余数据，文件已被篡改。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }
}
