using System.Text;
using CBox.Core;

ConsoleUi.Initialize();
if (Environment.GetEnvironmentVariable("CBOX_LOG") == "1")
{
    CbxLog.UseDebugDefaults();   // 开日志时按构建类型决定放开程度
}
CbxTestHooks.SlowMode = Environment.GetEnvironmentVariable("CBOX_SLOW") == "1";
if (CbxLog.Enabled)
{
    CbxLog.Snapshot();
}

return ConsoleUi.Run("CipherBox 解密端", () => Run(args));

static int Run(string[] args)
{
    string? password = null;
    string? overrideName = null;
    bool infoOnly = false;
    bool viewMode = false;
    bool folderMode = false;
    bool batchMode = false;
    int viewLimit = 64 * 1024;
    var positional = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-h" or "--help":
                PrintHelp();
                return 0;

            case "-v" or "--version":
                Console.WriteLine(CbxVersion.Display);
                return 0;

            case "--folder":
                folderMode = true;
                break;

            case "--batch":
                batchMode = true;
                break;

            case "--info":
                infoOnly = true;
                break;

            case "--view":
                viewMode = true;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int limit) && limit > 0)
                {
                    viewLimit = limit;
                    i++;
                }
                break;

            case "-p" or "--password":
                password = NextValue(args, ref i);
                break;

            case "--name":
                overrideName = NextValue(args, ref i);
                break;

            default:
                if (args[i].StartsWith('-'))
                {
                    throw new CbxException(UiText.Get("cli.unknown.option", args[i]));
                }
                positional.Add(args[i]);
                break;
        }
    }

    if (positional.Count == 0)
    {
        PrintHelp();
        return 2;
    }

    string inputPath = positional[0];
    string? outputDirectory = positional.Count > 1 ? positional[1] : null;

    // ---- 文件夹模式 ----
    //   归档（默认）：一个 .cbx 整包解到目录
    //   批量（--batch）：目录里每个 .cbx 各解一次
    if (folderMode || batchMode)
    {
        string folderInput = Path.GetFullPath(inputPath);
        string folderOutput = Path.GetFullPath(outputDirectory
            ?? (batchMode ? folderInput : Path.GetDirectoryName(folderInput) ?? "."));

        if (!batchMode && !File.Exists(folderInput))
        {
            throw new CbxException($"找不到要解密的归档：{folderInput}");
        }

        if (batchMode && !Directory.Exists(folderInput))
        {
            throw new CbxException($"找不到要解密的文件夹：{folderInput}");
        }

        ConsoleUi.Banner($"CipherBox 解密端  v{CbxVersion.Current}");
        Console.WriteLine(UiText.Get("cli.out.input", folderInput));
        Console.WriteLine(UiText.Get("cli.out.outputdir", folderOutput));
        Console.WriteLine($"模式       : {(batchMode ? "批量（每个 .cbx 各解一次）" : "归档（整包解开）")}");
        Console.WriteLine();

        password ??= ConsoleUi.ReadPassword("请输入口令：");
        if (password.Length == 0)
        {
            throw new CbxException(UiText.Get("error.password.empty"));
        }

        byte[] folderPassword = Encoding.UTF8.GetBytes(password);

        if (batchMode)
        {
            var batchReport = CbxFolder.DecryptFolder(
                folderInput, folderOutput, folderPassword);

            ConsoleUi.Info($"成功 {batchReport.Succeeded}，跳过 {batchReport.Skipped}，失败 {batchReport.Failed}");
            if (batchReport.Conflicted > 0)
            {
                Console.WriteLine($"同名冲突 {batchReport.Conflicted} 个：{batchReport.ConflictSamples}");
            }

            return batchReport.Failed > 0 ? 1 : 0;
        }

        int count = CbxArchive.ExtractAll(folderInput, folderOutput, folderPassword);
        ConsoleUi.Info($"解出 {count} 个文件。");
        Console.WriteLine(UiText.Get("cli.out.outputdir", folderOutput));
        return 0;
    }

    if (!File.Exists(inputPath))
    {
        throw new CbxException($"找不到要解密的文件：{inputPath}");
    }
    string fullInput = Path.GetFullPath(inputPath);

    if (infoOnly)
    {
        ConsoleUi.Banner("CipherBox 文件信息");
        Console.WriteLine(UiText.Get("cli.out.file", fullInput));
        Console.WriteLine();
        Console.WriteLine(Cbx.ReadHeader(fullInput).Describe());
        return 0;
    }

    if (viewMode)
    {
        password ??= ConsoleUi.ReadPassword("请输入口令：");
        if (password.Length == 0)
        {
            throw new CbxException(UiText.Get("error.password.empty"));
        }

        return ViewOnly(fullInput, password, viewLimit);
    }

    outputDirectory ??= Path.GetDirectoryName(fullInput) ?? ".";
    string fullOutputDirectory = Path.GetFullPath(outputDirectory);

    ConsoleUi.Banner($"CipherBox 解密端  v{CbxVersion.Current}");
    Console.WriteLine(UiText.Get("cli.out.input", fullInput));
    Console.WriteLine(UiText.Get("cli.out.outputdir", fullOutputDirectory));
    Console.WriteLine();

    password ??= ConsoleUi.ReadPassword("请输入口令：");
    if (password.Length == 0)
    {
        throw new CbxException(UiText.Get("error.password.empty"));
    }

    Console.WriteLine();
    var report = Cbx.DecryptFile(
        fullInput,
        fullOutputDirectory,
        Encoding.UTF8.GetBytes(password),
        overrideName);

    ConsoleUi.Info("解密完成。");
    Console.WriteLine(UiText.Get("cli.out.path", report.OutputPath));
    Console.WriteLine(UiText.Get("cli.out.originalname", report.OriginalName ?? UiText.Get("cli.out.notrecorded")));
    Console.WriteLine(UiText.Get("cli.out.plain", report.PlainSize));
    Console.WriteLine(UiText.Get("cli.out.chunks", report.ChunkCount));
    return 0;
}

/// <summary>
/// 只读查看：边解密边显示，明文不写盘。
/// 只显示前 limit 字节，超出的部分不读取（因此也不校验后续块）。
/// </summary>
static int ViewOnly(string path, string password, int limit)
{
    using var reader = Cbx.OpenReader(path, Encoding.UTF8.GetBytes(password));

    string name = reader.OriginalName ?? Path.GetFileNameWithoutExtension(path);
    Console.WriteLine();
    Console.WriteLine(UiText.Get("cli.view.title", name));
    Console.WriteLine(UiText.Get("cli.view.meta", reader.PlainSize, reader.ChunkCount, reader.ChunkSize));
    Console.WriteLine();

    using var buffer = new MemoryStream();
    bool stoppedEarly = false;

    while (buffer.Length < limit)
    {
        var block = reader.ReadBlock();
        if (block is null)
        {
            break;
        }

        buffer.Write(block, 0, block.Length);
    }

    stoppedEarly = reader.DeliveredBytes < reader.PlainSize;

    var data = buffer.ToArray();
    if (data.Length > limit)
    {
        data = data[..limit];
    }

    bool isTruncated = data.Length < reader.PlainSize;

    var decoded = CbxTextDecoder.Decode(data, truncated: isTruncated);

    if (decoded.IsBinary)
    {
        ConsoleUi.Warn("检测到二进制内容，这不是文本文件，无法只读查看。");
        if (decoded.Note is { Length: > 0 })
        {
            ConsoleUi.Warn(decoded.Note);
        }

        ConsoleUi.Info("请改用导出：dec <文件> <输出目录>");
        return 0;
    }

    string text = decoded.Text ?? string.Empty;
    Console.Write(text);
    if (!text.EndsWith('\n'))
    {
        Console.WriteLine();
    }

    Console.WriteLine();
    if (decoded.EncodingName is { Length: > 0 } && decoded.EncodingName != "UTF-8")
    {
        Console.WriteLine(UiText.Get("cli.view.encoding", decoded.EncodingName ?? "-"));
    }

    if (decoded.Note is { Length: > 0 })
    {
        ConsoleUi.Warn(decoded.Note);
    }

    if (stoppedEarly || data.Length < reader.PlainSize)
    {
        Console.WriteLine(UiText.Get("cli.view.truncated", data.Length, reader.PlainSize));
        Console.WriteLine(UiText.Get("cli.view.truncated.warn"));
    }

    return 0;
}

static void PrintHelp()
{
    Console.WriteLine(UiText.Get("cli.help.dec"));
}

static string NextValue(string[] args, ref int index)
{
    if (index + 1 >= args.Length)
    {
        throw new CbxException(UiText.Get("cli.missing.value", args[index]));
    }

    return args[++index];
}
