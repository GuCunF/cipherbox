namespace CBox.Core;

/// <summary>
/// 程序自己产生的、可以直接展示给用户的错误（口令错、文件坏、算法不支持等）。
/// 其它异常一律视为程序缺陷，不在这一层吞掉。
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

    /// <summary>
    /// 出错的数据块序号（从 0 开始）；与块无关的错误为 null。
    /// 有了它界面才能说"第 3 块校验失败"，而不是笼统的"文件已损坏"。
    /// </summary>
    public int? ChunkIndex { get; }
}
