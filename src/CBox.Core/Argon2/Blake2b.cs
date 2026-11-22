using System.Buffers.Binary;
using System.Numerics;

namespace CBox.Core;

/// <summary>
/// BLAKE2b 哈希（RFC 7693）。
///
/// 为什么自己写：项目坚持零第三方依赖，而 Argon2 的底层就是 BLAKE2b。
/// 只实现 Argon2 需要的部分——不带密钥、输出长度 1..64 字节。
///
/// 正确性靠标准测试向量验证，见 tests\Blake2bVectors。
/// </summary>
internal static class Blake2b
{
    /// <summary>BLAKE2b 的 IV，来自 RFC 7693 第 2.6 节（即 SHA-512 的初始值）。</summary>
    private static readonly ulong[] IV =
    [
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL,
        0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL,
        0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
    ];

    /// <summary>消息调度表。第 11、12 轮复用前两轮（见压缩函数里的取法）。</summary>
    private static readonly byte[][] Sigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
    ];

    /// <summary>块大小：128 字节。</summary>
    public const int BlockSize = 128;

    /// <summary>最大输出长度：64 字节。</summary>
    public const int MaxOutputLength = 64;

    // ---- 一次性哈希（输入完整内存中的字节）----

    /// <summary>算 BLAKE2b 摘要。</summary>
    /// <param name="input">要哈希的数据。</param>
    /// <param name="outputLength">输出字节数，1..64。</param>
    public static byte[] Hash(ReadOnlySpan<byte> input, int outputLength = 64)
    {
        if (outputLength is < 1 or > MaxOutputLength)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        var state = new State(outputLength);
        state.Update(input);
        return state.FinalizeHash();
    }

    /// <summary>
    /// 增量式状态。Argon2 的 H' 需要对「H0 + 两个 32 位整数」反复哈希，
    /// 用增量接口可以少拷一次数据。
    /// </summary>
    internal sealed class State
    {
        private readonly ulong[] _h = new ulong[8];
        private readonly byte[] _buffer = new byte[BlockSize * 2];
        private readonly int _outputLength;

        private int _bufferLength;
        private ulong _counterLow;
        private ulong _counterHigh;
        private bool _finalized;

        public State(int outputLength)
        {
            if (outputLength is < 1 or > MaxOutputLength)
            {
                throw new ArgumentOutOfRangeException(nameof(outputLength));
            }

            _outputLength = outputLength;

            // 参数块：digest_length | key_length<<8 | fanout<<16 | depth<<24
            // 这里不做密钥、单线程（fanout=1）、单层（depth=1）
            ulong parameterBlock = (ulong)outputLength | (1UL << 16) | (1UL << 24);

            for (int i = 0; i < 8; i++)
            {
                _h[i] = IV[i];
            }

            _h[0] ^= parameterBlock;
        }

        /// <summary>喂数据。</summary>
        public void Update(ReadOnlySpan<byte> data)
        {
            if (_finalized)
            {
                throw new InvalidOperationException("哈希已经结束，不能继续喂数据。");
            }

            int offset = 0;

            // 缓冲里已有数据：先凑满一块
            if (_bufferLength > 0)
            {
                int need = BlockSize - _bufferLength;

                if (data.Length <= need)
                {
                    data.CopyTo(_buffer.AsSpan(_bufferLength));
                    _bufferLength += data.Length;
                    return;
                }

                data[..need].CopyTo(_buffer.AsSpan(_bufferLength));
                data = data[need..];
                _bufferLength = 0;

                // 注意：最后一块必须留到 Finalize 时才压缩（带 f0=0xFFFFFFFF 标记），
                // 所以这里只在「后面还有数据」时才压。
                if (data.Length > 0)
                {
                    AddCounter(BlockSize);
                    Compress(_buffer.AsSpan(0, BlockSize), isLast: false);
                }
            }

            // 整块处理：留最后一块
            while (data.Length > BlockSize)
            {
                AddCounter(BlockSize);
                Compress(data[..BlockSize], isLast: false);

                data = data[BlockSize..];
            }

            if (data.Length > 0)
            {
                data.CopyTo(_buffer.AsSpan(0));
                _bufferLength = data.Length;
            }

            _ = offset;
        }

        /// <summary>把整数按小端追加进去（Argon2 的 H0 计算要用）。</summary>
        public void UpdateUInt32(uint value)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
            Update(tmp);
        }

        /// <summary>收尾，拿到摘要。</summary>
        public byte[] FinalizeHash()
        {
            if (!_finalized)
            {
                _finalized = true;

                // 最后一块：补零到块大小，带 f0=0xFFFFFFFF 标记后压缩
                _buffer.AsSpan(_bufferLength, BlockSize - _bufferLength).Clear();
                AddCounter((ulong)_bufferLength);
                Compress(_buffer.AsSpan(0, BlockSize), isLast: true);
            }

            var result = new byte[MAX_OUTPUT_BYTES_TMP];

            for (int i = 0; i < 8; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(i * 8, 8), _h[i]);
            }

            return result[.._outputLength];
        }

        private const int MAX_OUTPUT_BYTES_TMP = 64;

        private void AddCounter(ulong increment)
        {
            _counterLow += increment;

            if (_counterLow < increment)
            {
                _counterHigh++;
            }
        }

        /// <summary>压缩函数 F，RFC 7693 第 3.2 节。</summary>
        private void Compress(ReadOnlySpan<byte> block, bool isLast)
        {
            Span<ulong> m = stackalloc ulong[16];

            for (int i = 0; i < 16; i++)
            {
                m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
            }

            Span<ulong> v = stackalloc ulong[16];

            for (int i = 0; i < 8; i++)
            {
                v[i] = _h[i];
                v[i + 8] = IV[i];
            }

            v[12] ^= _counterLow;
            v[13] ^= _counterHigh;

            if (isLast)
            {
                v[14] = ~v[14];
            }

            for (int round = 0; round < 12; round++)
            {
                byte[] s = Sigma[round % 10];

                G(ref v[0], ref v[4], ref v[8], ref v[12], m[s[0]], m[s[1]]);
                G(ref v[1], ref v[5], ref v[9], ref v[13], m[s[2]], m[s[3]]);
                G(ref v[2], ref v[6], ref v[10], ref v[14], m[s[4]], m[s[5]]);
                G(ref v[3], ref v[7], ref v[11], ref v[15], m[s[6]], m[s[7]]);

                G(ref v[0], ref v[5], ref v[10], ref v[15], m[s[8]], m[s[9]]);
                G(ref v[1], ref v[6], ref v[11], ref v[12], m[s[10]], m[s[11]]);
                G(ref v[2], ref v[7], ref v[8], ref v[13], m[s[12]], m[s[13]]);
                G(ref v[3], ref v[4], ref v[9], ref v[14], m[s[14]], m[s[15]]);
            }

            for (int i = 0; i < 8; i++)
            {
                _h[i] ^= v[i] ^ v[i + 8];
            }
        }

        /// <summary>BLAKE2b 的 G 混合函数（带消息）。</summary>
        private static void G(ref ulong a, ref ulong b, ref ulong c, ref ulong d, ulong x, ulong y)
        {
            a = a + b + x;
            d = BitOperations.RotateRight(d ^ a, 32);
            c += d;
            b = BitOperations.RotateRight(b ^ c, 24);
            a = a + b + y;
            d = BitOperations.RotateRight(d ^ a, 16);
            c += d;
            b = BitOperations.RotateRight(b ^ c, 63);
        }
    }
}
