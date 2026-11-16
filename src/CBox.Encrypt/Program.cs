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

return ConsoleUi.Run("CipherBox 加密端", () => Run(args));

static int Run(string[] args)
{
    string? inputPath = null;
    string? outputPath = null;
    string? password = null;
    byte algId = CbxProfile.Default.AlgorithmId;
    byte kdfId = CbxProfile.Default.KdfId;
    uint? iterations = null;
    int chunkSize = CbxFormat.DefaultChunkSize;
    bool force = false;
    bool skipTypeCheck = false;
    bool fullEncryption = false;
    bool compatV1 = false;
    string? displayName = null;

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

            case "--list":
                PrintAlgorithms();
                return 0;

            case "-p" or "--password":
                password = NextValue(args, ref i);
                break;

            case "-a" or "--alg":
                algId = ParseByte(NextValue(args, ref i), "--alg");
                break;

            case "-k" or "--kdf":
                kdfId = ParseByte(NextValue(args, ref i), "--kdf");
                break;

            case "--iter":
                iterations = ParseUInt(NextValue(args, ref i), "--iter");
                break;

            case "-s" or "--strength":
                iterations = ParseStrength(NextValue(args, ref i));
                break;

            case "--full-encryption":
                fullEncryption = true;
                break;

            case "--display-name":
                displayName = NextValue(args, ref i);
                break;

            case "--compat-v1":
                compatV1 = true;
                break;

            case "--no-type-check":
                skipTypeCheck = true;
                break;

            case "-f" or "--force":
                force = true;
                break;

            case "--chunk":
                chunkSize = (int)ParseUInt(NextValue(args, ref i), "--chunk");
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

    inputPath = positional[0];
    if (positional.Count > 1)
    {
        outputPath = positional[1];
    }

    if (!File.Exists(inputPath))
    {
        throw new CbxException($"找不到要加密的文件：{inputPath}");
    }

    outputPath ??= inputPath + ".cbx";

    string fullInput = Path.GetFullPath(inputPath);
    string fullOutput = Path.GetFullPath(outputPath);
    if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase))
    {
        throw new CbxException(UiText.Get("cli.same.file"));
    }

    // 默认输出名就是「原文件名.cbx」，重复加密会覆盖上一次的密文。
    // 两次口令不同的话旧文件就再也解不开了，所以默认拒绝，必须显式 --force。
    if (File.Exists(fullOutput) && !force)
    {
        throw new CbxException(
            UiText.Get("cli.output.exists", fullOutput) + Environment.NewLine +
            UiText.Get("cli.output.exists.hint"));
    }

    // 先校验算法 ID，避免让用户输两遍口令之后才报错
    var cipher = CipherRegistry.Get(algId);
    var kdf = KdfRegistry.Get(kdfId);

    ConsoleUi.Banner($"CipherBox 加密端  v{CbxVersion.Current}");
    Console.WriteLine($"输入文件 : {fullInput}");
    Console.WriteLine($"输出文件 : {fullOutput}");
    Console.WriteLine($"算法     : {cipher.DisplayName} / {kdf.DisplayName}");
    Console.WriteLine($"文件大小 : {new FileInfo(fullInput).Length:N0} 字节");
    Console.WriteLine();

    password ??= ConsoleUi.ReadNewPassword();
    if (password.Length == 0)
    {
        throw new CbxException(UiText.Get("error.password.empty"));
    }

    Console.WriteLine();
    var report = Cbx.EncryptFile(
        fullInput,
        fullOutput,
        System.Text.Encoding.UTF8.GetBytes(password),
        algId,
        kdfId,
        iterations,
        chunkSize,
        progress: null,
        skipTypeCheck: skipTypeCheck,
        encryptName: fullEncryption,
        hideCategory: fullEncryption,
        writeCategory: !compatV1,
        displayName: displayName);

    ConsoleUi.Info(UiText.Get("cli.done.encrypt"));
    Console.WriteLine(UiText.Get("cli.out.path", report.OutputPath));
    Console.WriteLine(UiText.Get("cli.out.algorithm", report.Algorithm));
    Console.WriteLine(UiText.Get("cli.out.plain", report.PlainSize));
    Console.WriteLine(UiText.Get("cli.out.cipher", report.CipherSize));
    Console.WriteLine(UiText.Get("cli.out.chunks", report.ChunkCount));
    return 0;
}

static void PrintAlgorithms()
{
    Console.WriteLine("可用加密算法：");
    foreach (var cipher in CipherRegistry.All)
    {
        Console.WriteLine($"  {cipher.AlgorithmId}\t{cipher.DisplayName}");
    }

    Console.WriteLine();
    Console.WriteLine("可用密钥派生函数：");
    foreach (var kdf in KdfRegistry.All)
    {
        Console.WriteLine($"  {kdf.KdfId}\t{kdf.DisplayName}\t（默认迭代 {kdf.DefaultIterations:N0}）");
    }
}

static void PrintHelp()
{
    Console.WriteLine(UiText.Get("cli.help.enc"));
}

static string NextValue(string[] args, ref int index)
{
    if (index + 1 >= args.Length)
    {
        throw new CbxException(UiText.Get("cli.missing.value", args[index]));
    }

    return args[++index];
}

static byte ParseByte(string text, string option)
{
    if (!byte.TryParse(text, out byte value))
    {
        throw new CbxException($"{option} 需要一个数字，收到的是「{text}」。");
    }

    return value;
}

static uint ParseUInt(string text, string option)
{
    if (!uint.TryParse(text, out uint value))
    {
        throw new CbxException(UiText.Get("cli.bad.number", option, text));
    }

    return value;
}

/// <summary>强度档位 → PBKDF2 迭代次数。</summary>
static uint ParseStrength(string text)
{
    return text.ToLowerInvariant() switch
    {
        "quick" or "fast" or "1" => CbxFormat.QuickIterations,
        "standard" or "normal" or "2" => CbxFormat.StandardIterations,
        "strong" or "high" or "3" => CbxFormat.StrongIterations,
        _ => throw new CbxException(UiText.Get("cli.bad.strength", text)),
    };
}
