using System.Text;
using System.Text.Json;

namespace CBox.Core;

/// <summary>
/// 归档（文件夹加密的结果形态）：多个文件打包进**一个** .cbx。
///
/// 明文流布局（整体再按 1 MiB 分块做 AEAD）：
///   manifestLen u32 | manifest JSON | 文件1内容 | 文件2内容 | ...
///
/// 两个加密等级：
///   一般（previewable）：清单用同一把口令密钥加密，但**允许**输口令后只解前几块
///                       把清单读出来，用于预览目录结构与选择性导出。
///   完全：             previewable = false。程序不提供预览入口，
///                       必须整包解压，中途不显示任何条目名。
///
/// 两条都是产品层面的限制，不是密码学的：
/// 口令在手的人总能解出清单（格式是公开的）。文档里必须写清楚。
/// </summary>
public static class CbxArchive
{
    private const int ManifestLengthSize = 4;

    /// <summary>清单一律用这个编码；字段名短，减少体积。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public sealed record Entry(string Path, long Size);

    private sealed record ManifestDocument(Entry[] Entries);

    /// <summary>
    /// 扫描目录并生成清单（只 stat，不读内容）。
    /// </summary>
    public static IReadOnlyList<Entry> Scan(string sourceDirectory, bool recursive = true)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.GetFiles(sourceDirectory, "*", option)
            .Where(f => !f.EndsWith(".cbx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Entry(
                Cbx.MakeSafeRelativePath(Path.GetRelativePath(sourceDirectory, f)),
                new FileInfo(f).Length))
            .ToArray();
    }

    /// <summary>
    /// 把整个文件夹加密成一个 .cbx。
    /// 由于清单要写在最前面、而清单里包含每个文件的大小，必须先扫描一遍（只 stat）。
    /// 这样就不用"先压到临时文件"——明文一秒都不会落盘。
    /// </summary>
    public static EncryptReport? Create(
        string sourceDirectory,
        string outputPath,
        byte[] password,
        CbxProfile profile,
        bool previewable,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = Scan(sourceDirectory);

        if (entries.Count == 0)
        {
            throw new CbxException(CbxErrorCode.ArchiveEmpty, sourceDirectory);
        }

        byte[] manifest = BuildManifest(entries);

        // manifest 已经含 4 字节长度前缀，别再加一次
        long totalSize = manifest.LongLength + entries.Sum(e => e.Size);

        var archiveProfile = new CbxProfile
        {
            Name = profile.Name,
            AlgorithmId = profile.AlgorithmId,
            KdfId = profile.KdfId,
            Iterations = profile.Iterations,
            ChunkSize = profile.ChunkSize,
            SkipTypeCheck = profile.SkipTypeCheck,

            // 完全档连"这个归档叫什么名字"都不露：强制加密存名。
            // 一般档沿用用户设置（默认明文名，方便识别）
            EncryptName = previewable ? profile.EncryptName : true,

            HideCategory = true,          // 归档整体不对应某种内容类型
            WriteCategory = false,
            KeyFileFingerprint = profile.KeyFileFingerprint,   // 归档也要带钥匙，不然加密时选的钥匙白费
            Kind = CbxFormat.KindArchive,
            ArchiveFlags = previewable ? CbxFormat.ArchiveFlagPreviewable : (byte)0,
        };

        using var stream = new ArchiveSourceStream(sourceDirectory, entries, manifest, totalSize, cancellationToken);

        CbxLog.Info($"归档开始：{entries.Count} 个文件，明文共 {totalSize:N0} 字节，可预览={previewable}");

        return Cbx.EncryptStream(stream, totalSize, outputPath, password, archiveProfile,
            Path.GetFileName(sourceDirectory), progress);
    }

    /// <summary>
    /// 只解开头几块把清单读出来（一般档的预览用）。
    /// </summary>
    public static IReadOnlyList<Entry> ReadManifest(string cbxPath, byte[] password, byte[]? keyFileFingerprint = null)
    {
        using var reader = Cbx.OpenReader(cbxPath, password, keyFileFingerprint);

        if (!reader.Header.IsArchive)
        {
            throw new CbxException(CbxErrorCode.NotArchive);
        }

        using var head = new MemoryStream();
        int chunks = Math.Min(reader.ChunkCount, 2);   // 清单几乎都在第一块里

        for (int i = 0; i < chunks; i++)
        {
            var block = reader.ReadBlock();

            if (block is null)
            {
                break;
            }

            head.Write(block);

            if (TryParseManifest(head.ToArray(), out var preview, out _))
            {
                return preview;
            }
        }

        throw new CbxException(CbxErrorCode.CorruptedChunk);
    }

    /// <summary>
    /// 整包解开。按清单顺序把每个条目写到目标目录。
    /// onConflict 负责逐个询问（返回 true = 覆盖）。
    /// </summary>
    /// <summary>
    /// 整包解开。按清单顺序边解边写，内存里始终只有一块 1 MiB。
    /// onConflict 负责逐个询问（返回 true = 覆盖）。
    /// </summary>
    public static int ExtractAll(
        string cbxPath,
        string outputDirectory,
        byte[] password,
        Func<string, bool>? onConflict = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? onlyEntries = null,
        byte[]? keyFileFingerprint = null)
    {
        using var reader = Cbx.OpenReader(cbxPath, password, keyFileFingerprint);

        if (!reader.Header.IsArchive)
        {
            throw new CbxException(CbxErrorCode.NotArchive);
        }

        long total = reader.PlainSize;
        using var plain = new BlockStream(reader, cancellationToken);

        // 先读清单
        var entries = ReadManifestFrom(plain);

        int written = 0;
        var buffer = new byte[81920];

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string target = Path.Combine(outputDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            string? dir = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 只导出勾选的条目（预览窗口用）
            if (onlyEntries is not null && !onlyEntries.Contains(entry.Path))
            {
                SkipBytes(plain, entry.Size);
                continue;
            }

            bool keep = true;

            if (File.Exists(target) && onConflict is not null)
            {
                keep = onConflict(target);
            }

            if (!keep)
            {
                SkipBytes(plain, entry.Size);
                continue;
            }

            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long left = entry.Size;

                while (left > 0)
                {
                    int take = (int)Math.Min(buffer.Length, left);
                    int read = plain.Read(buffer, 0, take);

                    if (read <= 0)
                    {
                        throw new CbxException(CbxErrorCode.TruncatedFile);
                    }

                    file.Write(buffer, 0, read);
                    left -= read;
                }
            }

            written++;
            progress?.Report(total == 0 ? 1 : (double)plain.Position / total);
        }

        progress?.Report(1);
        return written;
    }

    /// <summary>跳过指定字节数（丢弃不写的条目）。</summary>
    private static void SkipBytes(Stream stream, long count)
    {
        var buffer = new byte[81920];

        while (count > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));

            if (read <= 0)
            {
                throw new CbxException(CbxErrorCode.TruncatedFile);
            }

            count -= read;
        }
    }

    private static IReadOnlyList<Entry> ReadManifestFrom(Stream plain)
    {
        var prefix = new byte[ManifestLengthSize];
        ReadExactlyOrThrow(plain, prefix);

        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prefix);

        if (length == 0 || length > 32 * 1024 * 1024)
        {
            throw new CbxException(CbxErrorCode.InvalidHeader);
        }

        var json = new byte[length];
        ReadExactlyOrThrow(plain, json);

        var doc = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions)
                  ?? throw new CbxException(CbxErrorCode.InvalidHeader);

        return doc.Entries;
    }

    private static void ReadExactlyOrThrow(Stream stream, byte[] buffer)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);

            if (read <= 0)
            {
                throw new CbxException(CbxErrorCode.TruncatedFile);
            }

            offset += read;
        }
    }

    /// <summary>把"按块解密"包装成普通只读流，供上面的顺序写入使用。</summary>
    private sealed class BlockStream : Stream
    {
        private readonly CbxDecryptReader _reader;
        private readonly CancellationToken _token;
        private byte[] _current = Array.Empty<byte>();
        private int _offset;
        private long _position;

        public BlockStream(CbxDecryptReader reader, CancellationToken token)
        {
            _reader = reader;
            _token = token;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _reader.PlainSize;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _token.ThrowIfCancellationRequested();

            if (count == 0)
            {
                return 0;
            }

            if (_offset >= _current.Length)
            {
                var block = _reader.ReadBlock();

                if (block is null)
                {
                    return 0;
                }

                _current = block;
                _offset = 0;
            }

            int take = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, take);
            _offset += take;
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// </summary>
    /// <summary>清单：长度前缀 + JSON（前缀计入长度）。</summary>
    private static byte[] BuildManifest(IReadOnlyList<Entry> entries)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new ManifestDocument(entries.ToArray()), JsonOptions);
        var result = new byte[ManifestLengthSize + json.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, (uint)json.Length);
        Buffer.BlockCopy(json, 0, result, ManifestLengthSize, json.Length);
        return result;
    }

    private static bool TryParseManifest(byte[] buffer, out IReadOnlyList<Entry> entries, out int consumed)
    {
        entries = Array.Empty<Entry>();
        consumed = 0;

        if (buffer.Length < ManifestLengthSize)
        {
            return false;
        }

        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer);

        if (length == 0 || length > 32 * 1024 * 1024 || buffer.Length < ManifestLengthSize + length)
        {
            return false;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<ManifestDocument>(
                buffer.AsSpan(ManifestLengthSize, (int)length), JsonOptions);

            if (doc?.Entries is null)
            {
                return false;
            }

            entries = doc.Entries;
            consumed = ManifestLengthSize + (int)length;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把"长度前缀 + 清单 + 若干文件"拼成一个只读流，避免先在磁盘上落明文。
    /// </summary>
    private sealed class ArchiveSourceStream : Stream
    {
        private readonly string _root;
        private readonly IReadOnlyList<Entry> _entries;
        private readonly byte[] _manifest;
        private readonly long _length;
        private readonly CancellationToken _token;

        private int _phase;          // 0 = 清单，1 = 文件
        private int _entryIndex = -1;
        private FileStream? _current;
        private long _remainingInEntry;
        private long _position;
        private int _manifestSent;

        public ArchiveSourceStream(
            string root, IReadOnlyList<Entry> entries, byte[] manifest, long length, CancellationToken token)
        {
            _root = root;
            _entries = entries;
            _manifest = manifest;
            _length = length;
            _token = token;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        /// <summary>推进到下一个"有内容"的条目；返回 false 表示已经到流末尾。</summary>
        private bool EnsureEntry()
        {
            while (_current is null)
            {
                _entryIndex++;

                if (_entryIndex >= _entries.Count)
                {
                    return false;
                }

                var entry = _entries[_entryIndex];
                string full = Path.Combine(_root, entry.Path.Replace('/', Path.DirectorySeparatorChar));

                if (entry.Size == 0)
                {
                    continue;   // 空文件不占流，直接跳过
                }

                _current = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                _remainingInEntry = entry.Size;
            }

            return true;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _token.ThrowIfCancellationRequested();

            int total = 0;

            while (count > 0)
            {
                if (_phase == 0)
                {
                    int take = (int)Math.Min(count, _manifest.Length - _manifestSent);

                    if (take > 0)
                    {
                        Buffer.BlockCopy(_manifest, _manifestSent, buffer, offset, take);
                        _manifestSent += take;
                        offset += take;
                        count -= take;
                        total += take;
                        _position += take;
                    }

                    if (_manifestSent >= _manifest.Length)
                    {
                        _phase = 1;
                    }

                    if (take == 0 && _phase == 0)
                    {
                        break;   // 理论上不会发生，兜底防死循环
                    }

                    continue;
                }

                if (!EnsureEntry())
                {
                    break;
                }

                int want = (int)Math.Min(count, _remainingInEntry);
                int read = _current!.Read(buffer, offset, want);

                if (read <= 0)
                {
                    throw new CbxException(CbxErrorCode.TruncatedFile, _current.Name);
                }

                offset += read;
                count -= read;
                total += read;
                _position += read;
                _remainingInEntry -= read;

                if (_remainingInEntry == 0)
                {
                    _current.Dispose();
                    _current = null;
                }
            }

            return total;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
