namespace CBox.Core;

/// <summary>
/// 非 UTF 编码解码器的注册表。
///
/// 移植时只需要在这里换一行：
/// <code>
/// LegacyDecoders.Use(new CodePagesDecoder());   // Linux / macOS
/// </code>
/// 调用方（文本查看器、命令行 --view）完全不用改。
/// </summary>
public static class LegacyDecoders
{
    private static ILegacyDecoder _current = CreateDefault();

    public static ILegacyDecoder Current => _current;

    public static void Use(ILegacyDecoder decoder)
    {
        _current = decoder;
    }

    private static ILegacyDecoder CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCodePageDecoder();
        }

        return new UnsupportedLegacyDecoder();
    }
}

/// <summary>不支持的平台上的占位实现：永远返回"解不了"，让调用方走二进制分支。</summary>
public sealed class UnsupportedLegacyDecoder : ILegacyDecoder
{
    public string Name => "不支持";
    public int CodePage => 0;
    public bool IsSupported => false;

    public bool TryDecode(byte[] data, out string text)
    {
        text = string.Empty;
        return false;
    }

    public bool TryEncode(string text, out byte[] data)
    {
        data = Array.Empty<byte>();
        return false;
    }
}
