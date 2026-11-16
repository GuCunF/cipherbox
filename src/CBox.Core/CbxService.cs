using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CBox.Core;

public sealed record EncryptReport(
    string OutputPath,
    string Algorithm,
    string Kdf,
    long PlainSize,
    long CipherSize,
    int ChunkCount,
    TimeSpan Elapsed,
    double MegabytesPerSecond,
    uint Iterations);

public sealed record DecryptReport(
    string OutputPath,
    string? OriginalName,
    long PlainSize,
    int ChunkCount,
    TimeSpan Elapsed,
    double MegabytesPerSecond);

/// <summary>
/// 加解密编排：头部构造、密钥派生、分块循环、完整性校验全在这里。
/// 两个客户端（加密端 / 解密端）以及未来的只读查看器都只调这里，不各自实现格式。
/// </summary>
public static class Cbx
{
    // ---------- 打开 ----------

    /// <summary>
    /// 打开一个流式解密器：逐块解出明文，可以边解边用，不落地成完整文件。
    /// 只读查看器靠它工作；解密端落盘则是它 + <see cref="CbxDecryptReader.CopyTo"/>。
    ///
    /// 注意：口令是否正确，要等第一块解出来才知道（AEAD 的固有特性）。
    /// </summary>
    public static CbxDecryptReader OpenReader(string path, byte[] password)
    {
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);

        try
        {
            var header = CbxHeader.Read(stream);
            var cipher = CipherRegistry.Get(header.AlgId);
            var kdf = KdfRegistry.Get(header.KdfId);
            header.Validate(cipher);

            var key = kdf.Derive(password, header.Salt, header.KdfIterations, cipher.KeySize);
            return new CbxDecryptReader(stream, header, cipher, key);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // ---------- 加密 ----------

    /// <summary>按参数组合加密。界面与将来的「一般模式」都走这个重载。</summary>
    public static EncryptReport EncryptFile(
        string sourcePath,
        string outputPath,
        byte[] password,
        CbxProfile profile,
        IProgress<double>? progress = null,
        string? displayName = null,
        bool allowPathInName = false,
        byte kind = CbxFormat.KindSingle,
        byte[]? batchId = null,
        byte archiveFlags = 0)
    {
        return EncryptFile(
            sourcePath,
            outputPath,
            password,
            profile.AlgorithmId,
            profile.KdfId,
            profile.Iterations,
            profile.ChunkSize,
            progress,
            profile.SkipTypeCheck,
            profile.EncryptName,
            profile.HideCategory,
            profile.WriteCategory,
            displayName ?? profile.DisplayName,
            allowPathInName,
            profile.Kind,
            profile.BatchId,
            profile.ArchiveFlags);
    }

    public static EncryptReport EncryptFile(
        string sourcePath,
        string outputPath,
        byte[] password,
        byte algId = CbxFormat.DefaultAlgId,
        byte kdfId = CbxFormat.DefaultKdfId,
        uint? iterations = null,
        int chunkSize = CbxFormat.DefaultChunkSize,
        IProgress<double>? progress = null,
        bool skipTypeCheck = false,
        bool encryptName = false,
        bool hideCategory = false,
        bool writeCategory = true,
        string? displayName = null,
        bool allowPathInName = false,
        byte kind = CbxFormat.KindSingle,
        byte[]? batchId = null,
        byte archiveFlags = 0)
    {
        using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);

        return EncryptCore(
            source, source.Length, Path.GetFileName(sourcePath), outputPath, password,
            algId, kdfId, iterations, chunkSize, progress,
            skipTypeCheck, encryptName, hideCategory, writeCategory, displayName, allowPathInName,
            kind, batchId, archiveFlags);
    }

    /// <summary>
    /// 从任意只读流加密（归档用）。
    /// 调用方保证 plainSize 与流的实际长度一致——分块循环按它决定块数。
    /// </summary>
    public static EncryptReport EncryptStream(
        Stream source,
        long plainSize,
        string outputPath,
        byte[] password,
        CbxProfile profile,
        string? displayName = null,
        IProgress<double>? progress = null)
    {
        return EncryptCore(
            source, plainSize, displayName ?? "归档", outputPath, password,
            profile.AlgorithmId, profile.KdfId, profile.Iterations, profile.ChunkSize, progress,
            profile.SkipTypeCheck, profile.EncryptName, profile.HideCategory, profile.WriteCategory,
            profile.DisplayName, false,
            profile.Kind, profile.BatchId, profile.ArchiveFlags);
    }

    /// <summary>
    /// 从内存直接加密。
    /// 编辑保存走这条路，**明文不落盘**——写成临时文件再加密会破坏"内容不写磁盘"的承诺。
    /// </summary>
    public static EncryptReport EncryptBytes(
        byte[] plaintext,
        string outputPath,
        byte[] password,
        CbxProfile profile,
        string? originalName = null,
        IProgress<double>? progress = null)
    {
        using var source = new MemoryStream(plaintext, writable: false);

        return EncryptCore(
            source, plaintext.LongLength, originalName ?? "未命名.txt", outputPath, password,
            profile.AlgorithmId, profile.KdfId, profile.Iterations, profile.ChunkSize, progress,
            profile.SkipTypeCheck, profile.EncryptName, profile.HideCategory, profile.WriteCategory,
            profile.DisplayName);
    }

    /// <summary>
    /// 清掉名字里的路径分隔符与非法字符。
    /// 这个名字会拿来当文件名（加密输出名、解密输出名），绝不能让它带上路径。
    /// </summary>
    public static string MakeSafeFileName(string name)
    {
        string cleaned = name.Trim();

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(c, '_');
        }

        cleaned = cleaned.Replace('/', '_').Replace('\\', '_');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "未命名";
        }

        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    /// <summary>
    /// 把相对路径清理成可以安全存进文件头的形式。
    /// 逐段清理、保留 "/" 作分隔符，丢弃 ".." 与空段——既保住层级，又不给路径穿越留口子。
    /// </summary>
    public static string MakeSafeRelativePath(string path)
    {
        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0 && segment != "." && segment != "..")
            .Select(MakeSafeFileName)
            .ToArray();

        return segments.Length == 0 ? "未命名" : string.Join("/", segments);
    }

    /// <summary>
    /// 写入前先判断目标位置到底哪里不行，给出**具体原因**而不是笼统一句"没有权限"。
    /// 实际踩过的坑：目标位置是个同名文件夹，却被报成权限问题，白白排查半天。
    /// </summary>
    public static void EnsureTargetUsable(string targetPath)
    {
        if (Directory.Exists(targetPath))
        {
            throw new CbxException(UiText.Get("error.target.isdir", targetPath));
        }

        string? directory = null;

        try
        {
            directory = Path.GetDirectoryName(targetPath);
        }
        catch
        {
            // 路径本身非法，交给下面统一报
        }

        if (string.IsNullOrEmpty(directory))
        {
            throw new CbxException(UiText.Get("error.target.noparent", targetPath));
        }

        if (!Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw new CbxException(UiText.Get("error.target.noparent", directory), ex);
            }
        }

        if (File.Exists(targetPath))
        {
            try
            {
                var attributes = File.GetAttributes(targetPath);

                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    throw new CbxException(UiText.Get("error.target.readonly", targetPath));
                }
            }
            catch (CbxException)
            {
                throw;
            }
            catch
            {
                // 读不到属性就继续，交给写入阶段
            }
        }

        // 最后才做真正的写入探测：排除以上所有可判定的原因后，才是权限问题
        try
        {
            string probe = Path.Combine(directory, $".cbox_probe_{Guid.NewGuid():N}.tmp");

            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new CbxException(UiText.Get("error.target.denied", directory), ex);
        }
    }

    /// <summary>读明文开头判类别：0=文本 1=图片 2=其他。读完把流位置复位。</summary>
    private static byte DetectCategory(Stream source, string originalName)
    {
        if (!source.CanSeek)
        {
            return 0;
        }

        try
        {
            var head = new byte[16];
            int read = source.Read(head, 0, head.Length);
            source.Position = 0;

            if (read <= 0)
            {
                return 0;
            }

            var kind = CbxFileType.Detect(head.AsSpan(0, read), originalName);

            if (kind.IsImage)
            {
                return 1;
            }

            return kind.Category == "unknown" ? (byte)0 : (byte)2;
        }
        catch
        {
            try { source.Position = 0; } catch { }
            return 0;
        }
    }

    private static EncryptReport EncryptCore(
        Stream source,
        long plainSize,
        string originalName,
        string outputPath,
        byte[] password,
        byte algId,
        byte kdfId,
        uint? iterations,
        int chunkSize,
        IProgress<double>? progress,
        bool skipTypeCheck,
        bool encryptName,
        bool hideCategory,
        bool writeCategory,
        string? displayName,
        bool allowPathInName = false,
        byte kind = CbxFormat.KindSingle,
        byte[]? batchId = null,
        byte archiveFlags = 0)
    {
        // 先把"目标位置到底哪里不行"判清楚，避免笼统报成权限问题
        EnsureTargetUsable(outputPath);

        var cipher = CipherRegistry.Get(algId);
        var kdf = KdfRegistry.Get(kdfId);

        if (chunkSize < CbxFormat.MinChunkSize || chunkSize > CbxFormat.MaxChunkSize)
        {
            throw new CbxException(
                $"块大小必须在 {CbxFormat.MinChunkSize:N0} ~ {CbxFormat.MaxChunkSize:N0} 字节之间。");
        }

        uint kdfIterations = iterations ?? kdf.DefaultIterations;

        uint chunkCount = plainSize == 0
            ? 0u
            : (uint)((plainSize + chunkSize - 1) / chunkSize);

        if (chunkCount == uint.MaxValue)
        {
            throw new CbxException("文件过大，超出当前格式支持范围。");
        }

        var salt = RandomNumberGenerator.GetBytes(CbxFormat.SaltSize);
        var baseNonce = RandomNumberGenerator.GetBytes(cipher.NonceSize);
        var key = kdf.Derive(password, salt, kdfIterations, cipher.KeySize);

        // 先看一眼明文开头，把"能不能预览"的类别记进头部——
        // 这样即使将来文件名也被加密，输口令之前仍然能决定要不要显示「只读查看」。
        // 全加密时故意写成假值「其他」，让所有全加密文件看起来一模一样
        byte category = hideCategory ? (byte)2 : DetectCategory(source, originalName);

        byte flags = CbxFormat.FlagHasName;

        if (writeCategory)
        {
            flags |= CbxFormat.FlagHasCategory;
        }

        if (category == 1)
        {
            flags |= CbxFormat.FlagCategoryImage;
        }
        else if (category == 2)
        {
            flags |= CbxFormat.FlagCategoryOther;
        }

        if (skipTypeCheck)
        {
            flags |= CbxFormat.FlagSkipTypeCheck;
        }

        var header = new CbxHeader
        {
            Version = CbxFormat.Version,
            AlgId = algId,
            KdfId = kdfId,
            Flags = flags,
            ChunkSize = (uint)chunkSize,
            PlainSize = (ulong)plainSize,
            ChunkCount = chunkCount,
            KdfIterations = kdfIterations,
            Salt = salt,
            BaseNonce = baseNonce,
        };

        // 显示名可以覆盖真实文件名（"存进文件的名字"与磁盘名解耦）
        string storedName = string.IsNullOrWhiteSpace(displayName) ? originalName : displayName!;
        storedName = allowPathInName ? MakeSafeRelativePath(storedName) : MakeSafeFileName(storedName);

        // 名字默认加密：攻击者拿到文件也看不出它是什么。
        // 加密走 AEAD，密文长度 = 原名 + 16 字节认证标签。
        if (encryptName && originalName.Length > 0)
        {
            header.NameBytes = CbxNameCodec.Encrypt(cipher, header, key, storedName);
            header.Flags |= CbxFormat.FlagEncryptedName;
        }
        else
        {
            header.NameBytes = System.Text.Encoding.UTF8.GetBytes(storedName);
        }

        header.OriginalName = storedName;   // 明文名只留在内存里，供日志与返回值使用
        header.Kind = kind;
        header.ArchiveFlags = archiveFlags;

        if (batchId is { Length: > 0 })
        {
            header.BatchId = batchId;
            header.Flags |= CbxFormat.FlagHasBatch;
        }


        var headerBytes = header.Serialize();
        var aadBase = SHA256.HashData(headerBytes);

        string tempPath = BuildTempPath(outputPath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        CbxLog.Info("================ 加密开始 ================");
        CbxLog.Info($"参数：算法={cipher.DisplayName}({algId}) KDF={kdf.DisplayName}({kdfId}) " +
                    $"迭代={kdfIterations} 块大小={chunkSize} 块数={chunkCount}");
        CbxLog.Info($"输入：{CbxLog.SafeName(originalName)}（{plainSize} 字节）");
        CbxLog.Info($"输出：{CbxLog.SafePath(outputPath)}");
        CbxLog.Info($"临时：{CbxLog.SafePath(tempPath)}");
        CbxLog.ProbeWritable(
            Path.GetDirectoryName(outputPath) is { Length: > 0 } outputDirectory ? outputDirectory : ".",
            "加密输出");

        try
        {
            using (var output = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, FileOptions.SequentialScan))
            {
                output.Write(headerBytes);

                var buffer = new byte[chunkSize];
                var lengthBytes = new byte[4];

                for (uint i = 0; i < chunkCount; i++)
                {
                    long offset = (long)i * chunkSize;
                    int want = (int)Math.Min(chunkSize, plainSize - offset);
                    source.ReadExactly(buffer, 0, want);

                    // 测试钩子要在处理这块**之前**执行：否则小文件会先跑完再报错，
                    // 进度条已经 100% 了才弹错误，看起来像"失败点算错了"
                    CbxTestHooks.Throttle((int)i, (int)chunkCount);
                    CbxTestHooks.MaybeFailEncrypt((int)i, (int)chunkCount);

                    var plain = want == chunkSize ? buffer : buffer[..want];
                    var nonce = CbxChunking.BuildNonce(baseNonce, i);
                    var aad = CbxChunking.BuildAad(aadBase, i);
                    var ciphertext = cipher.Encrypt(key, nonce, plain, aad);

                    BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)ciphertext.Length);
                    output.Write(lengthBytes);
                    output.Write(ciphertext);

                    progress?.Report((i + 1.0) / chunkCount);

                    if (chunkCount >= 4 && (i + 1) % (chunkCount / 4) == 0)
                    {
                        CbxLog.Debug($"加密进度 {(i + 1) * 100 / chunkCount}%（{i + 1}/{chunkCount} 块）");
                    }
                }
            }

            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            CbxLog.Error($"加密无权限 目标={CbxLog.SafePath(outputPath)} 临时={CbxLog.SafePath(tempPath)}", ex);
            CleanupOnFailure(tempPath);
            throw new CbxException(AccessDeniedHint(outputPath), ex);
        }
        catch (Exception ex)
        {
            CbxLog.Error($"加密失败 目标={CbxLog.SafePath(outputPath)} 临时={CbxLog.SafePath(tempPath)}", ex);
            CleanupOnFailure(tempPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var info = new FileInfo(outputPath);
        CbxLog.Info($"加密完成：密文 {info.Length} 字节，耗时 {stopwatch.ElapsedMilliseconds} ms，{DescribeSpeed(plainSize, stopwatch.Elapsed)}");
        CbxLog.Info("================ 加密结束 ================");
        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        double throughput = plainSize / 1024.0 / 1024.0 / seconds;

        return new EncryptReport(
            outputPath,
            cipher.DisplayName,
            kdf.DisplayName,
            plainSize,
            info.Length,
            (int)chunkCount,
            stopwatch.Elapsed,
            throughput,
            kdfIterations);
    }

    // ---------- 解密 ----------

    /// <summary>只读文件头（不需要口令），用于查看信息。</summary>
    public static CbxHeader ReadHeader(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);
        return CbxHeader.Read(stream);
    }

    public static DecryptReport DecryptFile(
        string sourcePath,
        string outputDirectory,
        byte[] password,
        string? overrideFileName = null,
        IProgress<double>? progress = null)
    {
        using var reader = OpenReader(sourcePath, password);
        var header = reader.Header;

        string fileName = overrideFileName
            ?? SanitizeFileName(header.OriginalName)
            ?? SafeStem(sourcePath) + ".decrypted";

        Directory.CreateDirectory(outputDirectory);
        string outputPath = UniquePath(Path.Combine(outputDirectory, fileName));

        // 目标定下来之后再判：只读 / 上级目录 / 真的没权限，各给各的说法
        EnsureTargetUsable(outputPath);
        string tempPath = BuildTempPath(outputPath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        CbxLog.Info("================ 解密开始 ================");
        CbxLog.Info($"参数：算法={CipherRegistry.Get(header.AlgId).DisplayName}({header.AlgId}) " +
                    $"KDF={KdfRegistry.Get(header.KdfId).DisplayName}({header.KdfId}) " +
                    $"迭代={header.KdfIterations} 块大小={header.ChunkSize} 块数={header.ChunkCount}");
        CbxLog.Info($"输入：{CbxLog.SafePath(sourcePath)}（明文 {header.PlainSize} 字节）");
        CbxLog.Info($"输出目录：{CbxLog.SafePath(outputDirectory)}");
        CbxLog.Info($"目标：{CbxLog.SafePath(outputPath)}");
        CbxLog.Info($"临时：{CbxLog.SafePath(tempPath)}");
        CbxLog.ProbeWritable(outputDirectory, "解密输出");

        try
        {
            using (var output = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, FileOptions.SequentialScan))
            {
                int index = 0;
                while (reader.ReadBlock() is { } block)
                {
                    output.Write(block, 0, block.Length);
                    index++;
                    if (header.ChunkCount > 0)
                    {
                        progress?.Report((double)index / header.ChunkCount);
                    }
                }
            }

            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            CbxLog.Error($"解密无权限 目标={CbxLog.SafePath(outputPath)} 临时={CbxLog.SafePath(tempPath)}", ex);
            CleanupOnFailure(tempPath);
            throw new CbxException(AccessDeniedHint(outputPath), ex);
        }
        catch (CbxException ex)
        {
            // 口令错 / 文件被改都会走到这里（对外提示故意不区分，日志里说清楚）
            string hint = reader.NextChunkIndex == 0
                ? "首块即认证失败，极可能是口令错误"
                : $"在第 {reader.NextChunkIndex + 1} 块失败，更可能是文件被篡改或截断";

            CbxLog.Warn($"解密失败 目标={CbxLog.SafePath(outputPath)} | {hint}");
            CbxLog.Error("解密失败详情", ex);
            CleanupOnFailure(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            CbxLog.Error($"解密失败 目标={CbxLog.SafePath(outputPath)} 临时={CbxLog.SafePath(tempPath)}", ex);
            CleanupOnFailure(tempPath);
            throw new CbxException($"解密过程出错：{ex.Message}", ex);
        }

        CbxLog.Info($"解密完成：输出 {CbxLog.SafePath(outputPath)}，耗时 {stopwatch.ElapsedMilliseconds} ms，{DescribeSpeed((long)header.PlainSize, stopwatch.Elapsed)}");
        CbxLog.Info("================ 解密结束 ================");

        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        double throughput = (long)header.PlainSize / 1024.0 / 1024.0 / seconds;

        return new DecryptReport(
            outputPath,
            header.HasName ? header.OriginalName : null,
            (long)header.PlainSize,
            (int)header.ChunkCount,
            stopwatch.Elapsed,
            throughput);
    }

    // ---------- 工具 ----------

    /// <summary>把文件头里的文件名洗干净：只留文件名本身，去掉任何路径成分。</summary>
    private static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string bare;
        try
        {
            bare = Path.GetFileName(name.Replace('\\', '/'));
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(bare) || bare is "." or "..")
        {
            return null;
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            bare = bare.Replace(invalid, '_');
        }

        return bare.Trim();
    }

    private static string SafeStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(stem) ? "output" : SanitizeFileName(stem) ?? "output";
    }

    /// <summary>目标已存在时自动加 (1)、(2)…，绝不覆盖用户已有文件。</summary>
    private static bool PathTaken(string path) => File.Exists(path) || Directory.Exists(path);

    private static string UniquePath(string path)
    {
        // 同名文件夹也算"被占用"：否则会走到"创建文件却撞上目录"的报错
        if (!PathTaken(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!PathTaken(candidate))
            {
                return candidate;
            }
        }

        throw new CbxException("同名文件过多，无法生成输出文件名。");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不该盖掉原始错误
        }
    }

    /// <summary>
    /// 临时文件名带随机后缀：避免上次失败留下的 .tmp 是只读或被占用，
    /// 导致这一次连写入都失败（表现为莫名其妙的"拒绝访问"）。
    /// </summary>
    private static string BuildTempPath(string outputPath)
    {
        return outputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
    }

    /// <summary>计算吞吐，只用于日志。</summary>
    private static string DescribeSpeed(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 0.01)
        {
            return "瞬间完成";
        }

        double megabytes = bytes / 1024.0 / 1024.0;
        return $"平均 {megabytes / elapsed.TotalSeconds:F1} MB/s";
    }

    /// <summary>
    /// 失败时的清理。
    /// 正式版：立刻删掉写了一半的临时文件，不留残缺数据。
    /// 测试版（KeepFailedOutput）：保留并记录路径，方便排查问题。
    /// </summary>
    private static void CleanupOnFailure(string tempPath)
    {
        if (CbxTestHooks.KeepFailedOutput)
        {
            CbxLog.Warn($"操作失败，已保留半成品供排查：{CbxLog.SafePath(tempPath)}");
            return;
        }

        TryDelete(tempPath);
    }

    /// <summary>把系统的"拒绝访问"翻译成用户能看懂的原因。</summary>
    private static string AccessDeniedHint(string path)
    {
        return
            $"没有权限写入这个位置：{path}" + Environment.NewLine +
            "常见原因（按可能性排序）：" + Environment.NewLine +
            "1. Windows 的「受控文件夹访问」拦住了本程序——" +
            "设置 → 隐私和安全性 → Windows 安全中心 → 病毒和威胁防护 → 勒索软件防护 → 受控文件夹访问，把本程序加入允许列表；" + Environment.NewLine +
            "2. 目标文件夹是只读的，或当前账户没有写权限；" + Environment.NewLine +
            "3. 同名文件正被其他程序占用。" + Environment.NewLine +
            "临时办法：把文件保存到非受保护的位置（例如 D 盘自建文件夹）试试。";
    }
}
