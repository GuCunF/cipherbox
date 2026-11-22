using System.Buffers.Binary;

namespace CBox.Core;

/// <summary>
/// Argon2id（RFC 9106）。
///
/// 为什么要自己写：PBKDF2 对显卡爆破几乎不设防——攻击者能用 GPU 每秒试上亿次。
/// Argon2 的防线是「吃内存」：每个候选口令都要占满几十 MiB，显存撑不住并发，
/// 爆破速度掉几个数量级。
///
/// 实现的是 Argon2id（混合型）：
///   - 第一轮前两个 slice 用数据无关寻址（防侧信道）
///   - 其余用数据相关寻址（防时间-内存权衡攻击）
///
/// 并行度 p 做成参数是为了能用 RFC 9106 的官方测试向量验证；
/// 实际使用时固定 p=1（桌面工具不需要抢多核）。
/// </summary>
internal static class Argon2Core
{
    /// <summary>算法标识：0=Argon2d，1=Argon2i，2=Argon2id。</summary>
    public const uint TypeArgon2id = 2;

    /// <summary>版本号 0x13（RFC 9106 定稿版）。</summary>
    public const uint Version = 0x13;

    /// <summary>单个块的字节数：1 KiB。</summary>
    public const int BlockBytes = 1024;

    /// <summary>单个块里的 64 位字数。</summary>
    public const int BlockWords = BlockBytes / 8;

    /// <summary>地址块里能放多少个伪随机数。</summary>
    private const int AddressesInBlock = 128;

    /// <summary>
    /// 派生密钥。
    /// </summary>
    /// <param name="password">口令。</param>
    /// <param name="salt">盐（建议 16 字节）。</param>
    /// <param name="iterations">迭代次数 τ。</param>
    /// <param name="memoryKiB">内存块数（KiB），会向下对齐到 4×p 的倍数。</param>
    /// <param name="outputLength">输出长度。</param>
    /// <param name="lanes">并行度 p，默认 1。</param>
    /// <param name="secret">可选的密钥 K（RFC 测试向量要用，实际使用留空）。</param>
    /// <param name="associatedData">可选的关联数据 X（同上）。</param>
    public static byte[] Derive(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        uint iterations,
        int memoryKiB,
        int outputLength,
        uint lanes = 1,
        ReadOnlySpan<byte> secret = default,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (iterations < 1)
        {
            throw new CbxException("Argon2 迭代次数至少为 1。");
        }

        if (lanes < 1)
        {
            throw new CbxException("Argon2 并行度至少为 1。");
        }

        if (salt.Length < 8)
        {
            throw new CbxException("Argon2 的盐至少 8 字节。");
        }

        if (outputLength < 4)
        {
            throw new CbxException("Argon2 输出长度至少 4 字节。");
        }

        // m' = 4p * floor(m / 4p)
        uint memory = (uint)memoryKiB;
        uint blocks = memory / (4 * lanes) * (4 * lanes);

        if (blocks < 8 * lanes)
        {
            throw new CbxException("Argon2 内存块数至少 8×并行度。");
        }

        byte[] h0 = ComputeH0(
            password, salt, iterations, blocks, (uint)outputLength, lanes, TypeArgon2id,
            secret, associatedData);

        // 平铺内存：第 (lane*laneLength + block) 个块占 128 个 ulong
        var memoryBlocks = new ulong[(long)blocks * BlockWords];

        try
        {
            FillMemory(memoryBlocks, h0, iterations, blocks, lanes, TypeArgon2id);

            // 最后一块：所有 lane 的末块异或
            uint segmentLength = blocks / (4 * lanes);
            uint laneLength = segmentLength * 4;

            var finalBlock = new ulong[BlockWords];
            long firstOffset = (long)(laneLength - 1) * BlockWords;

            for (int i = 0; i < BlockWords; i++)
            {
                finalBlock[i] = memoryBlocks[firstOffset + i];
            }

            for (uint lane = 1; lane < lanes; lane++)
            {
                long offset = (long)(lane * laneLength + laneLength - 1) * BlockWords;

                for (int i = 0; i < BlockWords; i++)
                {
                    finalBlock[i] ^= memoryBlocks[offset + i];
                }
            }

            var finalBytes = new byte[BlockBytes];

            for (int i = 0; i < BlockWords; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(finalBytes.AsSpan(i * 8, 8), finalBlock[i]);
            }

            return VariableLengthHash(finalBytes, outputLength);
        }
        finally
        {
            Array.Clear(memoryBlocks);
        }
    }

    /// <summary>
    /// 诊断用：把 H0 暴露给测试，用来和 RFC 的「Pre-hashing digest」逐字节比对。
    /// </summary>
    internal static byte[] DebugComputeH0(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        uint iterations,
        int memoryKiB,
        int outputLength,
        uint lanes,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> associatedData)
    {
        uint blocks = (uint)memoryKiB / (4 * lanes) * (4 * lanes);
        return ComputeH0(
            password, salt, iterations, blocks, (uint)outputLength, lanes, TypeArgon2id,
            secret, associatedData);
    }

    /// <summary>诊断用：把变长哈希单独暴露出来。</summary>
    internal static byte[] DebugVariableLengthHash(ReadOnlySpan<byte> input, int outputLength)
        => VariableLengthHash(input, outputLength);

    /// <summary>
    /// 诊断用：跑完填充后把整块内存交出来，方便和 RFC 的中间状态逐块比对。
    /// 调用方负责不再使用时清掉。
    /// </summary>
    internal static ulong[] DebugFillMemory(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        uint iterations,
        int memoryKiB,
        int outputLength,
        uint lanes,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> associatedData)
    {
        uint blocks = (uint)memoryKiB / (4 * lanes) * (4 * lanes);

        byte[] h0 = ComputeH0(
            password, salt, iterations, blocks, (uint)outputLength, lanes, TypeArgon2id,
            secret, associatedData);

        var memory = new ulong[(long)blocks * BlockWords];
        FillMemory(memory, h0, iterations, blocks, lanes, TypeArgon2id);

        return memory;
    }

    /// <summary>计算初始哈希 H0（RFC 9106 第 3.2 节）。</summary>
    private static byte[] ComputeH0(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        uint iterations,
        uint memoryKiB,
        uint outputLength,
        uint lanes,
        uint type,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> associatedData)
    {
        var state = new Blake2b.State(64);

        state.UpdateUInt32(lanes);          // p
        state.UpdateUInt32(outputLength);   // T
        state.UpdateUInt32(memoryKiB);      // m
        state.UpdateUInt32(iterations);     // t
        state.UpdateUInt32(Version);        // v
        state.UpdateUInt32(type);           // y

        state.UpdateUInt32((uint)password.Length);
        state.Update(password);

        state.UpdateUInt32((uint)salt.Length);
        state.Update(salt);

        state.UpdateUInt32((uint)secret.Length);
        state.Update(secret);

        state.UpdateUInt32((uint)associatedData.Length);
        state.Update(associatedData);

        return state.FinalizeHash();
    }

    /// <summary>填充内存矩阵（RFC 9106 第 3.4 节）。</summary>
    private static void FillMemory(
        ulong[] memory,
        byte[] h0,
        uint passes,
        uint blocks,
        uint lanes,
        uint type)
    {
        uint segmentLength = blocks / (4 * lanes);
        uint laneLength = segmentLength * 4;

        // ---- 每个 lane 的前两块由 H0 直接生成 ----
        var seed = new byte[h0.Length + 8];
        h0.CopyTo(seed.AsSpan());

        for (uint lane = 0; lane < lanes; lane++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(h0.Length), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(h0.Length + 4), lane);
            WriteBlock(memory, lane * laneLength, VariableLengthHash(seed, BlockBytes));

            BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(h0.Length), 1);
            WriteBlock(memory, lane * laneLength + 1, VariableLengthHash(seed, BlockBytes));
        }

        // ---- 逐轮、逐 slice、逐 lane 填充 ----
        for (uint pass = 0; pass < passes; pass++)
        {
            for (uint slice = 0; slice < 4; slice++)
            {
                for (uint lane = 0; lane < lanes; lane++)
                {
                    bool independent = pass == 0 && slice < 2;

                    // 独立寻址时，每 128 个 index 需要一个新的地址块
                    ulong[]? addressBlock = null;
                    uint addressCounter = 0;

                    for (uint index = 0; index < segmentLength; index++)
                    {
                        // 地址块必须在「跳过前两块」之前生成——
                        // 否则 index=0/1 被跳过后，index=2 会用到 null 地址块。
                        if (independent && index % AddressesInBlock == 0)
                        {
                            addressCounter = index / AddressesInBlock + 1;
                            addressBlock = GenerateAddressBlock(
                                pass, lane, slice, blocks, passes, type, addressCounter);
                        }

                        // 第一轮第一个 slice 的前两块已经初始化
                        if (pass == 0 && slice == 0 && index < 2)
                        {
                            continue;
                        }

                        uint currentBlock = slice * segmentLength + index;

                        uint previousBlock = index == 0
                            ? (slice == 0 ? laneLength - 1 : slice * segmentLength - 1)
                            : currentBlock - 1;

                        long previousOffset = (long)(lane * laneLength + previousBlock) * BlockWords;

                        // ---- 取伪随机数 ----
                        ulong pseudoRandom;

                        if (independent)
                        {
                            pseudoRandom = addressBlock![index % AddressesInBlock];
                        }
                        else
                        {
                            pseudoRandom = memory[previousOffset];
                        }

                        uint j1 = (uint)(pseudoRandom & 0xFFFFFFFF);
                        uint j2 = (uint)(pseudoRandom >> 32);

                        // ---- 参考 lane ----
                        uint referenceLane = j2 % lanes;

                        if (pass == 0 && slice == 0)
                        {
                            // 第一轮第一个 slice 不能引用别的 lane（别的 lane 还没填）
                            referenceLane = lane;
                        }

                        // ---- 参考块下标 ----
                        uint referenceIndex = IndexAlpha(
                            pass, slice, index, segmentLength, laneLength, j1,
                            referenceLane == lane);

                        long referenceOffset =
                            (long)(referenceLane * laneLength + referenceIndex) * BlockWords;

                        long currentOffset = (long)(lane * laneLength + currentBlock) * BlockWords;

                        // ---- 核心：B[cur] = G(B[prev], B[ref]) ----
                        // 第 2 轮起结果要 XOR 进旧块（RFC 第 6 步）
                        Compress(
                            memory.AsSpan((int)previousOffset, BlockWords),
                            memory.AsSpan((int)referenceOffset, BlockWords),
                            memory.AsSpan((int)currentOffset, BlockWords),
                            withXor: pass > 0);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 生成用于「数据无关寻址」的地址块（RFC 9106 第 3.4.1.2 节）。
    ///
    /// 公式是 address = G(0, G(0, Z))，其中 Z 是 1024 字节的输入块：
    ///   LE64(pass) || LE64(lane) || LE64(slice) || LE64(m') || LE64(t) || LE64(y) || LE64(counter) || 0...
    ///
    /// 注意：不是 H'，而是连做两次块压缩。
    /// </summary>
    private static ulong[] GenerateAddressBlock(
        uint pass,
        uint lane,
        uint slice,
        uint blocks,
        uint passes,
        uint type,
        uint counter)
    {
        var inputBytes = new byte[BlockBytes];
        var span = inputBytes.AsSpan();

        // 7 个 64 位整数（前 56 字节）
        BinaryPrimitives.WriteUInt64LittleEndian(span[0..], pass);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], lane);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], slice);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], blocks);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], passes);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], type);
        BinaryPrimitives.WriteUInt64LittleEndian(span[48..], counter);

        var inputWords = new ulong[BlockWords];

        for (int i = 0; i < BlockWords; i++)
        {
            inputWords[i] = BinaryPrimitives.ReadUInt64LittleEndian(inputBytes.AsSpan(i * 8, 8));
        }

        var zero = new ulong[BlockWords];
        var address = new ulong[BlockWords];

        // address = G(0, Z)
        Compress(zero, inputWords, address);

        // address = G(0, address)
        var second = new ulong[BlockWords];
        Compress(zero, address, second);

        return second;
    }

    /// <summary>
    /// 把伪随机数映射成参考块下标（对应参考实现的 index_alpha）。
    /// </summary>
    private static uint IndexAlpha(
        uint pass,
        uint slice,
        uint index,
        uint segmentLength,
        uint laneLength,
        uint pseudoRandom,
        bool sameLane)
    {
        int referenceAreaSize;

        if (pass == 0)
        {
            if (slice == 0)
            {
                referenceAreaSize = (int)index - 1;
            }
            else if (sameLane)
            {
                referenceAreaSize = (int)(slice * segmentLength + index) - 1;
            }
            else
            {
                referenceAreaSize = (int)(slice * segmentLength) + (index == 0 ? -1 : 0);
            }
        }
        else
        {
            if (sameLane)
            {
                referenceAreaSize = (int)(laneLength - segmentLength + index) - 1;
            }
            else
            {
                referenceAreaSize = (int)(laneLength - segmentLength) + (index == 0 ? -1 : 0);
            }
        }

        if (referenceAreaSize < 1)
        {
            // 理论上不会发生（调用方已跳过前两块），防御性返回 0
            return 0;
        }

        // 平方后取高位：让分布更均匀
        ulong relative = pseudoRandom;
        relative = (relative * relative) >> 32;
        relative = (ulong)referenceAreaSize - 1 -
                   (((ulong)referenceAreaSize * relative) >> 32);

        uint start = 0;

        if (pass != 0)
        {
            start = slice == 3 ? 0 : (slice + 1) * segmentLength;
        }

        return (uint)((start + (uint)relative) % laneLength);
    }

    /// <summary>
    /// Argon2 的块压缩 G(X, Y) = P(X⊕Y) ⊕ (X⊕Y)。
    ///
    /// withXor 对应 RFC 第 6 步：第 2 轮及以后的结果要 XOR 进**原有的块内容**，
    /// 而不是直接覆盖（B[i][j] = G(...) XOR B[i][j]）。
    /// </summary>
    private static void Compress(
        ReadOnlySpan<ulong> x,
        ReadOnlySpan<ulong> y,
        Span<ulong> destination,
        bool withXor = false)
    {
        Span<ulong> r = stackalloc ulong[BlockWords];

        for (int i = 0; i < BlockWords; i++)
        {
            r[i] = x[i] ^ y[i];
        }

        Span<ulong> q = stackalloc ulong[BlockWords];
        r.CopyTo(q);

        // 8 行，每行 16 个字
        for (int i = 0; i < 8; i++)
        {
            Permute(q.Slice(i * 16, 16));
        }

        // 8 列：每列由 8 对相邻字组成（相邻两字一组，组间跨 16 字）
        // column 在循环外分配，避免每轮都往栈上堆
        Span<ulong> column = stackalloc ulong[16];

        for (int i = 0; i < 8; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                column[k * 2] = q[i * 2 + k * 16];
                column[k * 2 + 1] = q[i * 2 + 1 + k * 16];
            }

            Permute(column);

            for (int k = 0; k < 8; k++)
            {
                q[i * 2 + k * 16] = column[k * 2];
                q[i * 2 + 1 + k * 16] = column[k * 2 + 1];
            }
        }

        for (int i = 0; i < BlockWords; i++)
        {
            ulong value = q[i] ^ r[i];
            destination[i] = withXor ? destination[i] ^ value : value;
        }
    }

    /// <summary>对 16 个字做一轮置换（Argon2 的 P 操作）。</summary>
    private static void Permute(Span<ulong> v)
    {
        Mix(ref v[0], ref v[4], ref v[8], ref v[12]);
        Mix(ref v[1], ref v[5], ref v[9], ref v[13]);
        Mix(ref v[2], ref v[6], ref v[10], ref v[14]);
        Mix(ref v[3], ref v[7], ref v[11], ref v[15]);

        Mix(ref v[0], ref v[5], ref v[10], ref v[15]);
        Mix(ref v[1], ref v[6], ref v[11], ref v[12]);
        Mix(ref v[2], ref v[7], ref v[8], ref v[13]);
        Mix(ref v[3], ref v[4], ref v[9], ref v[14]);
    }

    /// <summary>
    /// Argon2 的混合函数。与 BLAKE2b 的 G 不同，这里多了一项
    /// 2×(低32位 a)×(低32位 b)，用来增加非线性。
    /// </summary>
    private static void Mix(ref ulong a, ref ulong b, ref ulong c, ref ulong d)
    {
        a = a + b + 2UL * (ulong)(uint)a * (ulong)(uint)b;
        d = System.Numerics.BitOperations.RotateRight(d ^ a, 32);

        c = c + d + 2UL * (ulong)(uint)c * (ulong)(uint)d;
        b = System.Numerics.BitOperations.RotateRight(b ^ c, 24);

        a = a + b + 2UL * (ulong)(uint)a * (ulong)(uint)b;
        d = System.Numerics.BitOperations.RotateRight(d ^ a, 16);

        c = c + d + 2UL * (ulong)(uint)c * (ulong)(uint)d;
        b = System.Numerics.BitOperations.RotateRight(b ^ c, 63);
    }

    /// <summary>
    /// 变长哈希 H'（RFC 9106 第 3.3 节）。
    ///
    /// 规则：T ≤ 64 一次算完。
    /// T &gt; 64 时先算 V1 = H^64(LE32(T)||A)，**只取前 32 字节**，
    /// 然后链式续接，每次也都只取 32 字节，最后一块才用剩余长度。
    ///
    /// 注意这里容易写错：不是每次取 64 字节。RFC 给的是
    ///   r = ceil(T/32) - 2，V1..Vr 各取 32 字节，最后一块长 T-32r
    /// </summary>
    private static byte[] VariableLengthHash(ReadOnlySpan<byte> input, int outputLength)
    {
        // T ≤ 64：H^T(LE32(T) || input)，一次到位
        if (outputLength <= 64)
        {
            var state = new Blake2b.State(outputLength);
            state.UpdateUInt32((uint)outputLength);
            state.Update(input);
            return state.FinalizeHash();
        }

        var result = new byte[outputLength];

        // V1 = H^64(LE32(T) || input)，取前 32 字节
        byte[] current;

        {
            var state = new Blake2b.State(64);
            state.UpdateUInt32((uint)outputLength);
            state.Update(input);
            current = state.FinalizeHash();
        }

        const int halfBlock = 32;
        Buffer.BlockCopy(current, 0, result, 0, halfBlock);

        int produced = halfBlock;
        int remaining = outputLength - halfBlock;

        // 中间的块：H^64(前一块)，仍取前 32 字节
        while (remaining > 64)
        {
            current = Blake2b.Hash(current, 64);
            Buffer.BlockCopy(current, 0, result, produced, halfBlock);
            produced += halfBlock;
            remaining -= halfBlock;
        }

        // 最后一块：H^remaining(前一块)，整块取走
        byte[] tail = Blake2b.Hash(current, remaining);
        Buffer.BlockCopy(tail, 0, result, produced, remaining);

        return result;
    }

    /// <summary>把 1024 字节写进第 blockIndex 个块。</summary>
    private static void WriteBlock(ulong[] memory, uint blockIndex, byte[] bytes)
    {
        long offset = (long)blockIndex * BlockWords;

        for (int i = 0; i < BlockWords; i++)
        {
            memory[offset + i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8, 8));
        }
    }
}
