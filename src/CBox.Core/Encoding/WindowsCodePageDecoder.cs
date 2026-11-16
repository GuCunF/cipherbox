using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CBox.Core;

/// <summary>
/// Windows 实现：直接调内核的 <c>MultiByteToWideChar</c> / <c>WideCharToMultiByte</c>。
///
/// 好处：
///   - 零第三方依赖（不需要 NuGet 包）
///   - 与记事本、WPS 用的是同一套代码页数据，结果一致
/// 代价：
///   - 只能在 Windows 上跑（本项目当前就是 Windows 专用）
///
/// 用 <c>MB_ERR_INVALID_CHARS</c> 让非法字节序列直接失败，
/// 而不是产生一堆替换字符——宁可判定为"未知编码"，也不显示乱码。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCodePageDecoder : ILegacyDecoder
{
    private const uint CodePageGbk = 936;
    private const uint MbErrInvalidChars = 0x00000008;
    // WC_ERR_INVALID_CHARS 只对 65001/1200/1201 有效，对 936 传它会直接失败，所以用 0
    private const uint WcNoFlags = 0;

    public string Name => "GBK";

    public int CodePage => (int)CodePageGbk;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool TryDecode(byte[] data, out string text)
    {
        text = string.Empty;

        if (!IsSupported || data.Length == 0)
        {
            return false;
        }

        try
        {
            int charCount = MultiByteToWideChar(
                CodePageGbk, MbErrInvalidChars, data, data.Length, null!, 0);

            if (charCount <= 0)
            {
                return false;
            }

            var buffer = new char[charCount];
            int written = MultiByteToWideChar(
                CodePageGbk, MbErrInvalidChars, data, data.Length, buffer, charCount);

            if (written <= 0)
            {
                return false;
            }

            text = new string(buffer, 0, written);
            return true;
        }
        catch (Exception)
        {
            // P/Invoke 出错也不能让调用方崩，退化为"无法解码"
            return false;
        }
    }

    public bool TryEncode(string text, out byte[] data)
    {
        data = Array.Empty<byte>();

        if (!IsSupported)
        {
            return false;
        }

        if (text.Length == 0)
        {
            return true;
        }

        try
        {
            int byteCount = WideCharToMultiByte(
                CodePageGbk, WcNoFlags, text, text.Length, null!, 0, IntPtr.Zero, IntPtr.Zero);

            if (byteCount <= 0)
            {
                return false;
            }

            var buffer = new byte[byteCount];
            int written = WideCharToMultiByte(
                CodePageGbk, WcNoFlags, text, text.Length, buffer, byteCount, IntPtr.Zero, IntPtr.Zero);

            if (written <= 0)
            {
                return false;
            }

            data = buffer;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        [In] byte[] multiByteStr,
        int multiByteCount,
        [Out] char[]? wideCharStr,
        int wideCharCount);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WideCharToMultiByte(
        uint codePage,
        uint flags,
        [In] string wideCharStr,
        int wideCharCount,
        [Out] byte[]? multiByteStr,
        int multiByteCount,
        IntPtr defaultChar,
        IntPtr usedDefaultChar);
}
