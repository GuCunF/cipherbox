namespace CBox.Core;

/// <summary>文件夹加密的三种方案，对应界面上三个挡位。</summary>
public enum FolderMode
{
    /// <summary>镜像：保留目录结构，每个文件原地对应一个 .cbx。不隐藏任何东西。</summary>
    Mirror = 0,

    /// <summary>扁平：全部放同一层，相对路径加密后存进各自的文件头。隐藏结构，仍可单独取。</summary>
    Flat = 1,

    /// <summary>打包：整个文件夹合成一个 .cbx。隐藏一切，但一般要整包解开。</summary>
    Archive = 2,
}

public sealed record FolderReport(
    int Total,
    int Succeeded,
    int Skipped,
    int Failed,
    string OutputDirectory,
    int Copied = 0,
    string Formats = "",
    int Conflicted = 0,
    string ConflictSamples = "");

/// <summary>
/// 文件夹级加解密。
///
/// 三种方案共用同一套遍历与错误处理，差异只在"输出路径怎么算"：
///   Mirror  → 输出目录 + 相对路径 + ".cbx"
///   Flat    → 输出目录 + 短哈希 + ".cbx"（相对路径写进文件头）
///   Archive → 单文件（另行实现）
///
/// 共同的安全规则：
///   1. 目标已存在一律跳过，绝不覆盖
///   2. 已经是 .cbx 的源文件不再处理
///   3. 单个文件失败不影响其他，最后给汇总
/// </summary>
public static class CbxFolder
{
    private const string CbxExtension = ".cbx";

    /// <summary>
    /// 解密一个文件夹（对应方案 A 镜像）。
    ///
    /// 输入目录里递归找所有 .cbx，按相对路径还原目录树，文件名去掉 .cbx 后缀。
    /// 目标已存在一律跳过，绝不覆盖。
    ///
    /// 口令错误会让每个文件都失败，所以**第一个文件失败就整体中止**，
    /// 避免刷出上百条相同错误。
    /// </summary>
    public static FolderReport DecryptFolder(
        string sourceDirectory,
        string outputDirectory,
        byte[] password,
        bool recursive = true,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new CbxException($"文件夹不存在：{sourceDirectory}");
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var files = Directory.GetFiles(sourceDirectory, "*" + CbxExtension, option)
            .Where(f => f.EndsWith(CbxExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int succeeded = 0;
        int skipped = 0;
        int failed = 0;

        // 先扫一遍文件头（不需要口令）统计格式，告诉用户这堆密文是不是同一种
        string formats = ScanFormats(files);
        int conflicted = 0;
        var conflictSamples = new List<string>();

        Directory.CreateDirectory(outputDirectory);

        // 同一批密文的盐值通常相同，缓存住推导结果能省下每个文件 0.13 秒的重复计算
        DerivedKeyCache.Clear();

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = files[i];
            string relative = Path.GetRelativePath(sourceDirectory, source);

            // 扁平模式的真实路径藏在文件头里（形如 "2025/子/c.txt"）；
            // 镜像模式的头里只有文件名，于是回退到 .cbx 的相对路径。
            string targetRelative = relative[..^CbxExtension.Length];

            try
            {
                string? storedName = ReadStoredName(source, password);

                // 什么时候相信头里的名字？
                //   1. 带 "/" → 一定是扁平模式存的相对路径
                //   2. 存储名的最后一段与 .cbx 文件名（去掉 .cbx）不同 → 扁平模式
                //      （镜像模式下两者恰好相同，此时应该用 .cbx 的目录结构）
                string cbxStem = Path.GetFileNameWithoutExtension(relative);
                string storedLeaf = Path.GetFileName(storedName ?? string.Empty);

                bool trustStored = !string.IsNullOrWhiteSpace(storedName) &&
                                   (storedName!.Contains('/') ||
                                    !cbxStem.Equals(storedLeaf, StringComparison.OrdinalIgnoreCase));

                if (trustStored)
                {
                    targetRelative = Cbx.MakeSafeRelativePath(storedName!);
                }
            }
            catch
            {
                // 名字读不出来不致命，按 .cbx 的相对路径还原即可
            }

            targetRelative = targetRelative.Replace('/', Path.DirectorySeparatorChar);
            string targetDirectory = Path.Combine(outputDirectory, Path.GetDirectoryName(targetRelative) ?? string.Empty);
            string targetFile = Path.GetFileName(targetRelative);

            progress?.Report((i, files.Length, targetFile));

            string candidate = Path.Combine(targetDirectory, targetFile);
            if (File.Exists(candidate))
            {
                skipped++;
                conflicted++;
                conflictSamples.Add(Path.GetFileName(source));
                CbxLog.Warn($"文件夹解密同名冲突（保留先到的）：{CbxLog.SafePath(candidate)} ← {CbxLog.SafePath(source)}");
                continue;
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);
                Cbx.DecryptFile(source, targetDirectory, password, targetFile);
                succeeded++;
            }
            catch (CbxException ex)
            {
                // 夹着不同口令/算法的文件夹里，个别文件解不开是正常的，先记账继续。
                // 只有"一个都没成功过、而且已经连错 3 个"才判定为口令错误并中止，
                // 否则会把整堆能解的文件也一起放弃。
                failed++;
                CbxLog.Warn($"文件夹解密失败：{CbxLog.SafePath(source)} - {ex.Message}");

                if (succeeded == 0 && failed >= 3)
                {
                    throw new CbxException(
                        $"解密失败（已连续 {failed} 个文件解不开）。" + Environment.NewLine +
                        "通常是口令不对；如果这些文件是用不同口令加密的，请分开处理。",
                        ex);
                }
            }
            catch (Exception ex)
            {
                failed++;
                CbxLog.Warn($"文件夹解密失败：{CbxLog.SafePath(source)} - {ex.Message}");
            }
        }

        progress?.Report((files.Length, files.Length, string.Empty));

        // 批量结束就把密钥从缓存里涂掉，不让它继续留在内存
        DerivedKeyCache.Clear();

        return new FolderReport(
            files.Length, succeeded, skipped, failed, outputDirectory,
            Formats: formats,
            Conflicted: conflicted,
            ConflictSamples: string.Join(", ", conflictSamples.Take(5)));
    }
    /// <summary>
    /// 读出文件头里记录的名字（可能带相对路径）。
    /// 名字没加密时直接读头，省掉一次密钥派生；加密了才走需要口令的读取器。
    /// </summary>
    private static string? ReadStoredName(string cbxPath, byte[] password)
    {
        try
        {
            var header = Cbx.ReadHeader(cbxPath);

            if (header.HasName && !header.EncryptedName)
            {
                return header.OriginalName;
            }
        }
        catch
        {
            // 落到下面的读取器
        }

        using var reader = Cbx.OpenReader(cbxPath, password);
        return reader.OriginalName;
    }

    /// <summary>
    /// 统计一批 .cbx 用了哪些算法/KDF（只读文件头，不需要口令）。
    /// 用来提示"这个文件夹里混着不同格式"，避免用户拿一个口令去试。
    /// </summary>
    private static string ScanFormats(string[] cbxFiles)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string file in cbxFiles)
        {
            try
            {
                var header = Cbx.ReadHeader(file);
                string algName = CipherRegistry.IsSupported(header.AlgId)
                    ? CipherRegistry.Get(header.AlgId).DisplayName
                    : $"未知算法({header.AlgId})";

                if (!CipherRegistry.IsSupported(header.AlgId))
                {
                    unsupported.Add($"algId={header.AlgId}");
                }

                string kdfName = $"KDF{header.KdfId}";
                string key = $"{algName} / {kdfName}";

                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            catch
            {
                counts["无法读取头"] = counts.TryGetValue("无法读取头", out int n2) ? n2 + 1 : 1;
            }
        }

        var parts = counts.Select(pair => $"{pair.Key} × {pair.Value}").ToList();

        if (unsupported.Count > 0)
        {
            parts.Add("不支持的算法：" + string.Join(", ", unsupported));
        }

        return string.Join("；", parts);
    }

    public static IReadOnlyList<string> ListEncryptableFiles(string directory, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            throw new CbxException($"文件夹不存在：{directory}");
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.GetFiles(directory, "*", option)
            .Where(f => !f.EndsWith(CbxExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>算出某个源文件对应的输出路径。</summary>
    public static string ResolveOutputPath(string sourceRoot, string sourceFile, string outputRoot, FolderMode mode)
    {
        string relative = Path.GetRelativePath(sourceRoot, sourceFile);

        if (mode == FolderMode.Mirror)
        {
            return Path.Combine(outputRoot, relative + CbxExtension);
        }

        // 扁平模式：文件名用相对路径的短哈希，真实路径加密存进文件头
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative))
        )[..16].ToLowerInvariant();

        return Path.Combine(outputRoot, hash + CbxExtension);
    }

    /// <summary>
    /// 加密一个文件夹。目前实现了 Mirror 与 Flat；Archive 还没做。
    /// </summary>
    public static FolderReport EncryptFolder(
        string sourceDirectory,
        string outputDirectory,
        byte[] password,
        CbxProfile profile,
        FolderMode mode,
        bool recursive = true,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == FolderMode.Archive)
        {
            throw new CbxException("打包模式尚未实现。");
        }

        var files = ListEncryptableFiles(sourceDirectory, recursive);

        // 源目录里已经存在的 .cbx 不重新加密，但**要原样拷贝**到输出目录。
        // 否则"加密一个夹着密文的文件夹"会得到不完整的镜像：老密文凭空消失，看起来像丢文件。
        var existingCipherFiles = Directory.GetFiles(
                sourceDirectory,
                "*" + CbxExtension,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(CbxExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int succeeded = 0;
        int skipped = 0;
        int failed = 0;
        int copied = 0;

        Directory.CreateDirectory(outputDirectory);

        foreach (string cipherFile in existingCipherFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(sourceDirectory, cipherFile);
            string target = Path.Combine(outputDirectory, relative);

            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(cipherFile, target);
                copied++;
            }
            catch (Exception ex)
            {
                failed++;
                CbxLog.Warn($"拷贝已有密文失败：{CbxLog.SafePath(cipherFile)} - {ex.Message}");
            }
        }

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = files[i];
            string output = ResolveOutputPath(sourceDirectory, source, outputDirectory, mode);

            progress?.Report((i, files.Count, Path.GetFileName(source)));

            if (File.Exists(output))
            {
                skipped++;
                CbxLog.Info($"文件夹加密跳过（已存在）：{CbxLog.SafePath(source)}");
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // Flat 模式把相对路径当作"显示名"写进文件头，解密时据此还原目录。
                // allowPathInName 让清理函数保留 "/" 分隔符，否则层级会被下划线吞掉。
                bool flat = mode == FolderMode.Flat;
                string? storedName = flat ? Path.GetRelativePath(sourceDirectory, source) : null;

                Cbx.EncryptFile(source, output, password, profile, null, storedName, flat);

                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                CbxLog.Warn($"文件夹加密失败：{CbxLog.SafePath(source)} - {ex.Message}");
            }
        }

        progress?.Report((files.Count, files.Count, string.Empty));

        return new FolderReport(files.Count, succeeded, skipped, failed, outputDirectory, copied);
    }
}

/// <summary>
/// 批量加密：一个文件夹里放 N 个独立的 .cbx，每个都等效于单文件加密，
/// 但同批共享一个随机批次 ID。解密端靠它判断"有没有混入别的批次"。
/// </summary>
public static class CbxBatch
{
    public sealed record VerifyResult(bool Ok, string Message);

    /// <summary>加密一批文件，输出目录里每个文件仍是普通 .cbx（可以单独解密）。</summary>
    public static FolderReport Encrypt(
        string sourceDirectory,
        string outputDirectory,
        byte[] password,
        CbxProfile profile,
        bool recursive = true,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(
                sourceDirectory,
                "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".cbx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        byte[] batchId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(CbxFormat.BatchIdSize);

        var batchProfile = new CbxProfile
        {
            Name = profile.Name,
            AlgorithmId = profile.AlgorithmId,
            KdfId = profile.KdfId,
            Iterations = profile.Iterations,
            ChunkSize = profile.ChunkSize,
            SkipTypeCheck = profile.SkipTypeCheck,
            EncryptName = profile.EncryptName,
            HideCategory = profile.HideCategory,
            WriteCategory = profile.WriteCategory,
            Kind = CbxFormat.KindBatch,
            BatchId = batchId,
        };

        int succeeded = 0;
        int skipped = 0;
        int failed = 0;

        Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = files[i];
            string relative = Path.GetRelativePath(sourceDirectory, source);
            string output = Path.Combine(outputDirectory, relative + ".cbx");
            progress?.Report((i, files.Length, Path.GetFileName(source)));

            if (File.Exists(output))
            {
                skipped++;
                CbxLog.Info($"批量加密跳过（已存在）：{CbxLog.SafePath(source)}");
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                Cbx.EncryptFile(source, output, password, batchProfile, null, relative, true);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                CbxLog.Warn($"批量加密失败：{CbxLog.SafePath(source)} - {ex.Message}");
            }
        }

        progress?.Report((files.Length, files.Length, string.Empty));

        return new FolderReport(files.Length, succeeded, skipped, failed, outputDirectory);
    }

    /// <summary>
    /// 校验：**先逐个验口令，再看批次是否一致**。
    /// 任何一项不通过都返回失败原因，调用方不应开始解密。
    /// </summary>
    public static VerifyResult Verify(string directory, byte[] password, bool recursive = true)
    {
        var files = Directory.GetFiles(
                directory,
                "*.cbx",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            return new VerifyResult(false, "这个文件夹里没有 .cbx 文件。");
        }

        foreach (string file in files)
        {
            try
            {
                using var reader = Cbx.OpenReader(file, password);
                reader.ReadBlock();
            }
            catch (CbxException ex)
            {
                return new VerifyResult(false,
                    $"口令验证失败：{Path.GetFileName(file)}" + Environment.NewLine + ex.Message);
            }
        }

        byte[]? expected = null;

        foreach (string file in files)
        {
            CbxHeader header;

            try
            {
                header = Cbx.ReadHeader(file);
            }
            catch (CbxException ex)
            {
                return new VerifyResult(false, $"无法读取文件头：{Path.GetFileName(file)} - {ex.Message}");
            }

            if (!header.IsBatchMember)
            {
                return new VerifyResult(false,
                    $"{Path.GetFileName(file)} 不是批量加密的结果（可能是单文件加密），整批解密已终止。");
            }

            if (expected is null)
            {
                expected = header.BatchId;
                continue;
            }

            if (!expected.SequenceEqual(header.BatchId))
            {
                return new VerifyResult(false,
                    $"{Path.GetFileName(file)} 属于另一个批次，整批解密已终止。");
            }
        }

        return new VerifyResult(true, $"校验通过：{files.Length} 个文件，批次一致，口令正确。");
    }

    /// <summary>批量解密：先校验，通过后才逐个解密。任何一个不合格就整体不开始。</summary>
    public static FolderReport Decrypt(
        string sourceDirectory,
        string outputDirectory,
        byte[] password,
        bool recursive = true,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var check = Verify(sourceDirectory, password, recursive);

        if (!check.Ok)
        {
            throw new CbxException(check.Message);
        }

        var files = Directory.GetFiles(
                sourceDirectory,
                "*.cbx",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int succeeded = 0;
        int skipped = 0;
        int failed = 0;

        Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = files[i];
            string relative = Path.GetRelativePath(sourceDirectory, source);
            string targetRelative = relative[..^4];

            try
            {
                var header = Cbx.ReadHeader(source);

                if (!string.IsNullOrWhiteSpace(header.OriginalName))
                {
                    targetRelative = Cbx.MakeSafeRelativePath(header.OriginalName);
                }
            }
            catch
            {
                // 读不到就按 .cbx 的名字来
            }

            targetRelative = targetRelative.Replace('/', Path.DirectorySeparatorChar);
            string targetFile = Path.GetFileName(targetRelative);
            string targetDir = Path.Combine(outputDirectory, Path.GetDirectoryName(targetRelative) ?? string.Empty);

            progress?.Report((i, files.Length, targetFile));

            if (File.Exists(Path.Combine(targetDir, targetFile)))
            {
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(targetDir);
                Cbx.DecryptFile(source, targetDir, password, targetFile);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                CbxLog.Warn($"批量解密失败：{CbxLog.SafePath(source)} - {ex.Message}");
            }
        }

        progress?.Report((files.Length, files.Length, string.Empty));

        return new FolderReport(files.Length, succeeded, skipped, failed, outputDirectory);
    }
}