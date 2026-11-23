namespace CBox.Core;

/// <summary>
/// 程序自己产生的、可以直接展示给用户的错误（口令错、文件坏、算法不支持等）。
/// 其它异常一律视为程序缺陷，不在这一层吞掉。
///
/// <para>
/// 从 2.2.2 起，用户可见的错误会带上 <see cref="Code"/>，
/// 让界面层可以按当前语言翻译，而不是被迫显示硬编码的中文。
/// 但 <see cref="Exception.Message"/> **仍然生成中文**——
/// 这样还没改造的调用点行为完全不变，错误码化可以分批推进。
/// </para>
/// </summary>
public sealed class CbxException : Exception
{
    public CbxException(string message) : base(message) { }

    public CbxException(string message, Exception inner) : base(message, inner) { }

    public CbxException(string message, int chunkIndex) : base(message)
    {
        ChunkIndex = chunkIndex;
    }

    public CbxException(string message, int chunkIndex, Exception inner) : base(message, inner)
    {
        ChunkIndex = chunkIndex;
    }

    /// <summary>带错误码构造。消息仍按中文生成，保持向后兼容。</summary>
    public CbxException(CbxErrorCode code, params object[] args)
        : base(Describe(code, args))
    {
        Code = code;
        Args = args;
    }

    /// <summary>带错误码 + 内部异常构造。</summary>
    public CbxException(CbxErrorCode code, Exception inner, params object[] args)
        : base(Describe(code, args), inner)
    {
        Code = code;
        Args = args;
    }

    /// <summary>带错误码 + 数据块序号构造。</summary>
    public CbxException(CbxErrorCode code, int chunkIndex, params object[] args)
        : base(Describe(code, args))
    {
        Code = code;
        Args = args;
        ChunkIndex = chunkIndex;
    }

    /// <summary>
    /// 错误码。为 null 表示这条错误还没码化（老代码路径）。
    /// </summary>
    public CbxErrorCode? Code { get; }

    /// <summary>
    /// 消息模板的参数。界面层翻译时要用它们填充占位符。
    /// </summary>
    public object[]? Args { get; }

    /// <summary>
    /// 出错的数据块序号（从 0 开始）；与块无关的错误为 null。
    /// 有了它界面才能说"第 3 块校验失败"，而不是笼统的"文件已损坏"。
    /// </summary>
    public int? ChunkIndex { get; }

    /// <summary>
    /// 按错误码生成中文消息。
    ///
    /// <para>
    /// 这里是**唯一的**中文来源，界面层将来会改成查词条；
    /// 在改造完成前，<see cref="Exception.Message"/> 仍然是这些文字。
    /// </para>
    /// </summary>
    private static string Describe(CbxErrorCode code, object[] args)
    {
        string Arg(int i) => i < args.Length ? args[i]?.ToString() ?? string.Empty : string.Empty;

        return code switch
        {
            CbxErrorCode.WrongPassword =>
                "口令错误，或者文件被改动过（两者在密码学上无法区分）。",

            CbxErrorCode.KeyFileRequired =>
                "这个文件需要密钥文件才能解密：请同时提供加密时用的那个文件。",

            CbxErrorCode.KeyFileWrong =>
                "密钥文件不对：和加密时用的不是同一个文件。",

            CbxErrorCode.KeyFileEmpty =>
                $"密钥文件是空的（0 字节）：{Arg(0)}\n\n" +
                "空文件不能当钥匙——所有空文件算出来的内容都一样，" +
                "等于随便找个空文件就能开门，第二重保护会失效。",

            CbxErrorCode.KeyFileUnreadable =>
                $"读不了密钥文件：{Arg(0)}\n\n{Arg(1)}",

            CbxErrorCode.KeyFileNotFound =>
                $"找不到密钥文件：{Arg(0)}",

            CbxErrorCode.NotCbxFile =>
                "这不是本程序的加密文件（文件头标识不匹配）。",

            CbxErrorCode.UnsupportedVersion =>
                $"这个文件用了更新的容器版本（v{Arg(0)}），当前程序最高支持 v{Arg(1)}。请升级后再试。",

            CbxErrorCode.UnsupportedAlgorithm =>
                $"文件用的加密算法不受支持（算法编号 {Arg(0)}）。",

            CbxErrorCode.UnsupportedKdf =>
                $"文件用的密钥派生算法不受支持（编号 {Arg(0)}）。",

            CbxErrorCode.CorruptedChunk =>
                "文件已损坏：有数据块没通过完整性校验。",

            CbxErrorCode.TruncatedFile =>
                "文件不完整：可能被截断或传输中断。",

            CbxErrorCode.FileTooLarge =>
                "文件过大，超出当前格式支持范围。",

            CbxErrorCode.InvalidHeader =>
                "文件头异常，无法解析。",

            CbxErrorCode.AccessDenied =>
                $"没有权限写入这个位置：{Arg(0)}\n\n" +
                "常见原因（按可能性排序）：\n" +
                "1. Windows 的「受控文件夹访问」拦住了本程序——" +
                "设置 → 隐私和安全性 → Windows 安全中心 → 病毒和威胁防护 → 勒索软件防护 → 受控文件夹访问，把本程序加入允许列表；\n" +
                "2. 目标文件夹是只读的，或当前账户没有写权限；\n" +
                "3. 同名文件正被其他程序占用。\n" +
                "临时办法：把文件保存到非受保护的位置（例如 D 盘自建文件夹）试试。",

            CbxErrorCode.OutputDirectoryMissing =>
                $"输出目录不存在：{Arg(0)}",

            CbxErrorCode.SourceNotFound =>
                $"找不到这个文件：{Arg(0)}",

            CbxErrorCode.NotArchive =>
                "这个文件不是归档，请用「单个文件」模式解密。",

            CbxErrorCode.ArchiveEntryMissing =>
                $"归档里没有这一项：{Arg(0)}",

            CbxErrorCode.ArchiveEmpty =>
                $"这个文件夹里没有可归档的文件：{Arg(0)}",

            CbxErrorCode.InvalidParameter =>
                $"参数不合法：{Arg(0)}",

            _ => "发生了一个未分类的错误。",
        };
    }
}
