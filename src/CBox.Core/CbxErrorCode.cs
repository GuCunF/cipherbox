namespace CBox.Core;

/// <summary>
/// 可直接展示给用户的错误码。
///
/// <para>
/// 为什么要这个：<see cref="CbxException"/> 里的消息是硬编码中文，
/// 英文界面下一旦出错，弹出来的还是中文——做了双语却在最该说明问题的地方失效。
/// </para>
///
/// <para>
/// 设计取舍：只给**用户能看到**的错误码化。
/// 参数校验、算法不支持这类「程序内部防御」不进码表——
/// 它们出现就说明代码有 bug，不该指望用户看懂。
/// </para>
/// </summary>
public enum CbxErrorCode
{
    // ---------- 口令与密钥 ----------

    /// <summary>口令错误（首块即认证失败）。</summary>
    WrongPassword,

    /// <summary>文件需要密钥文件，但用户没提供。</summary>
    KeyFileRequired,

    /// <summary>密钥文件不对（提供了，但对不上）。</summary>
    KeyFileWrong,

    /// <summary>密钥文件是 0 字节，不能当钥匙。</summary>
    KeyFileEmpty,

    /// <summary>读不了密钥文件（权限或占用）。</summary>
    KeyFileUnreadable,

    /// <summary>密钥文件不存在。</summary>
    KeyFileNotFound,

    // ---------- 文件格式 ----------

    /// <summary>不是本程序生成的加密文件（magic 不匹配）。</summary>
    NotCbxFile,

    /// <summary>容器版本不支持（比本程序新）。</summary>
    UnsupportedVersion,

    /// <summary>加密算法不支持。</summary>
    UnsupportedAlgorithm,

    /// <summary>密钥派生算法不支持。</summary>
    UnsupportedKdf,

    /// <summary>某个数据块校验失败（带块序号）。</summary>
    CorruptedChunk,

    /// <summary>文件被截断或重排（块数对不上）。</summary>
    TruncatedFile,

    /// <summary>文件过大，超出格式支持范围。</summary>
    FileTooLarge,

    /// <summary>文件头字段异常（长度越界等）。</summary>
    InvalidHeader,

    // ---------- 文件系统 ----------

    /// <summary>没有权限写入目标位置。</summary>
    AccessDenied,

    /// <summary>目标路径不存在。</summary>
    OutputDirectoryMissing,

    /// <summary>源文件不存在。</summary>
    SourceNotFound,

    // ---------- 归档 ----------

    /// <summary>这个文件不是归档，不能按归档方式解开。</summary>
    NotArchive,

    /// <summary>归档里没有指定的条目。</summary>
    ArchiveEntryMissing,

    /// <summary>要归档的文件夹是空的，没东西可打。</summary>
    ArchiveEmpty,

    // ---------- 其他 ----------

    /// <summary>参数不对（用户传了非法值）。</summary>
    InvalidParameter,
}
