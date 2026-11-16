namespace CBox.Core;

/// <summary>
/// 非 UTF 编码处理器的接口。
///
/// 需要同时支持解码和编码：查看时解码，编辑保存时要按原编码写回，
/// 否则 GBK 文件一保存就变乱码。
/// </summary>
public interface ILegacyDecoder
{
    string Name { get; }

    /// <summary>编码对应的代码页号，例如 GBK = 936。</summary>
    int CodePage { get; }

    bool IsSupported { get; }

    bool TryDecode(byte[] data, out string text);

    /// <summary>把文本按本编码编码回字节。</summary>
    bool TryEncode(string text, out byte[] data);
}
