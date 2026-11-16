using System.Text;

namespace CBox.Core;

public sealed record TextDecodeResult(
    string? Text,
    string? EncodingName,
    bool IsBinary,
    string? Note,
    int CodePage = 0,
    bool HasBom = false);

/// <summary>
/// 把一段字节判定为文本并解码。判定顺序：
///   BOM → 严格 UTF-8 → 平台的非 UTF 编码（Windows 上是 GBK）→ 判定为二进制
///
/// 任何一步失败都不会返回"半截乱码"，宁可说"这不是文本"。
/// </summary>
public static class CbxTextDecoder
{
    private const int Utf8CodePage = 65001;
    private const int Utf16LeCodePage = 1200;
    private const int Utf16BeCodePage = 1201;

    public static TextDecodeResult Decode(byte[] data, bool truncated = false)
    {
        if (data.Length == 0)
        {
            return new TextDecodeResult(string.Empty, "UTF-8", IsBinary: false, Note: null, Utf8CodePage);
        }

        // 1. BOM
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Decode(Encoding.UTF8, data[3..], "UTF-8 (BOM)", allowFallback: false, Utf8CodePage, hasBom: true);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Decode(Encoding.Unicode, data[2..], "UTF-16 LE", allowFallback: false, Utf16LeCodePage, hasBom: true);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Decode(Encoding.BigEndianUnicode, data[2..], "UTF-16 BE", allowFallback: false, Utf16BeCodePage, hasBom: true);
        }

        // 2. 明显是二进制就直接结束，不用试编码
        if (LooksBinary(data))
        {
            return new TextDecodeResult(null, null, IsBinary: true, Note: null);
        }

        // 3. 严格 UTF-8（截断的尾部先去掉，否则必然失败）
        var utf8Data = truncated ? TrimIncompleteUtf8(data) : data;
        try
        {
            string text = new UTF8Encoding(false, true).GetString(utf8Data);
            return new TextDecodeResult(text, "UTF-8", IsBinary: false, Note: null, Utf8CodePage);
        }
        catch (DecoderFallbackException)
        {
            // 继续尝试其他编码
        }

        // 4. 平台的非 UTF 编码（Windows 上 = GBK）
        //    截断点可能落在双字节字符中间。GBK 不像 UTF-8 有自同步特性，
        //    半个字符会让整个解码失败，所以失败后回退 1~3 个字节再试。
        var legacy = LegacyDecoders.Current;
        if (legacy.IsSupported)
        {
            var legacyData = utf8Data;
            int maxBackoff = truncated ? 3 : 0;

            for (int drop = 0; drop <= maxBackoff; drop++)
            {
                if (drop > 0)
                {
                    legacyData = legacyData[..^1];
                }

                if (legacyData.Length == 0)
                {
                    break;
                }

                if (legacy.TryDecode(legacyData, out string legacyText))
                {
                    string note = drop > 0
                        ? $"按 {legacy.Name} 解码（末尾截断，已回退 {drop} 字节）"
                        : $"按 {legacy.Name} 解码";

                    return new TextDecodeResult(legacyText, legacy.Name, IsBinary: false, note, legacy.CodePage);
                }
            }
        }

        return new TextDecodeResult(
            null,
            null,
            IsBinary: true,
            Note: "既不是 UTF-8 也无法按本机代码页解码，已按二进制处理");
    }

    /// <summary>
    /// 按解码时识别出的编码把文本编码回字节。编辑保存必须用它，
    /// 否则 GBK 文件会被写成 UTF-8，下次打开就是乱码。
    /// </summary>
    public static byte[] EncodeForSave(string text, int codePage, bool includeBom)
    {
        byte[] body;

        switch (codePage)
        {
            case Utf16LeCodePage:
                body = Encoding.Unicode.GetBytes(text);
                return includeBom ? Prepend(body, 0xFF, 0xFE) : body;

            case Utf16BeCodePage:
                body = Encoding.BigEndianUnicode.GetBytes(text);
                return includeBom ? Prepend(body, 0xFE, 0xFF) : body;

            case Utf8CodePage:
            case 0:
                body = new UTF8Encoding(false).GetBytes(text);
                return includeBom ? Prepend(body, 0xEF, 0xBB, 0xBF) : body;

            default:
                var legacy = LegacyDecoders.Current;
                if (legacy.IsSupported && legacy.CodePage == codePage && legacy.TryEncode(text, out byte[] encoded))
                {
                    return encoded;
                }

                // 兜底：用 UTF-8 写，至少内容不丢（调用方会记录这条路径）
                return new UTF8Encoding(false).GetBytes(text);
        }
    }

    private static byte[] Prepend(byte[] body, params byte[] prefix)
    {
        var result = new byte[prefix.Length + body.Length];
        prefix.CopyTo(result, 0);
        body.CopyTo(result, prefix.Length);
        return result;
    }

    /// <summary>粗判二进制：出现 NUL 或不可打印控制字符的比例过高。</summary>
    public static bool LooksBinary(byte[] data)
    {
        int end = Math.Min(data.Length, 8192);
        int control = 0;

        for (int i = 0; i < end; i++)
        {
            byte b = data[i];

            if (b == 0)
            {
                return true;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                control++;
            }
        }

        return end > 0 && control * 100 / end > 5;
    }

    /// <summary>截断时把末尾不完整的多字节序列切掉，避免显示半个汉字。</summary>
    public static byte[] TrimIncompleteUtf8(byte[] data)
    {
        int n = data.Length;

        for (int back = 1; back <= 4 && back <= n; back++)
        {
            byte b = data[n - back];

            if ((b & 0xC0) == 0x80)
            {
                continue;   // 续字节，继续往前找首字节
            }

            int need = b < 0x80 ? 1
                : (b & 0xE0) == 0xC0 ? 2
                : (b & 0xF0) == 0xE0 ? 3
                : (b & 0xF8) == 0xF0 ? 4
                : 1;

            return need > back ? data[..(n - back)] : data;
        }

        return data;
    }

    private static TextDecodeResult Decode(
        Encoding encoding,
        byte[] data,
        string name,
        bool allowFallback,
        int codePage,
        bool hasBom)
    {
        try
        {
            var strict = (Encoding)encoding.Clone();
            strict.DecoderFallback = allowFallback
                ? DecoderFallback.ReplacementFallback
                : DecoderFallback.ExceptionFallback;

            return new TextDecodeResult(strict.GetString(data), name, IsBinary: false, Note: null, codePage, hasBom);
        }
        catch (DecoderFallbackException)
        {
            return new TextDecodeResult(null, null, IsBinary: true, Note: $"{name} 解码失败");
        }
    }
}
