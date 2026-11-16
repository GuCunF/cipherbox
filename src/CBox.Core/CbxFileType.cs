using System.Text;

namespace CBox.Core;

/// <summary>文件类型判定结果。Categories 取值：text / image / document / media / archive / executable / unknown。</summary>
public sealed record FileKind(
    string Category,
    string NameCode,
    string FallbackName,
    string HintKey = "",
    string FallbackHint = "",
    string? NameArg = null)
{
    /// <summary>显示名：优先取词条 filetype.{NameCode}，没有就用中文兜底。</summary>
    public string DisplayName => UiText.GetOrFallback("filetype." + NameCode, FallbackName, NameArg);

    /// <summary>提示语：优先取 HintKey 词条，没有就用中文兜底。</summary>
    public string Hint => string.IsNullOrEmpty(HintKey)
        ? FallbackHint
        : UiText.GetOrFallback(HintKey, FallbackHint);

    public bool IsImage => Category == "image";
    public bool IsDocument => Category == "document";
}

/// <summary>
/// 根据文件头（魔数）判定类型。扩展名只作为辅助线索——它能被随便改，字节不会。
/// </summary>
public static class CbxFileType
{
    /// <summary>识别常见图片格式；不是图片返回 null。</summary>
    public static string? DetectImage(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
        {
            return "PNG";
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "JPEG";
        }

        if (head.Length >= 4 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38)
        {
            return "GIF";
        }

        if (head.Length >= 2 && head[0] == 0x42 && head[1] == 0x4D)
        {
            return "BMP";
        }

        if (head.Length >= 12 &&
            head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
            head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
        {
            return "WebP";
        }

        return null;
    }

    /// <summary>判定文件类型。name 是文件头里记录的原始文件名，用于区分 docx / zip 这类共用魔数的格式。</summary>
    public static FileKind Detect(ReadOnlySpan<byte> head, string? name)
    {
        string extension = GetExtension(name);

        // 图片
        string? image = DetectImage(head);
        if (image is not null)
        {
            return new FileKind("image", "image", "{0} 图片", NameArg: image);
        }

        // PDF：%PDF
        if (StartsWith(head, "%PDF"u8))
        {
            return new FileKind("document", "pdf", "PDF 文档",
                "filetype.hint.document", "PDF 需要专门的渲染引擎，本程序不做这件事。请导出后用 PDF 阅读器打开。");
        }

        // 旧版 Office（doc/xls/ppt）：OLE2 容器 D0 CF 11 E0
        if (StartsWith(head, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            (string officeCode, string label) = extension switch
            {
                ".doc" => ("doc97", "Word 文档（97-2003）"),
                ".xls" => ("xls97", "Excel 表格（97-2003）"),
                ".ppt" => ("ppt97", "PowerPoint 演示文稿（97-2003）"),
                _ => ("office", "Office 旧版文档"),
            };

            return new FileKind("document", officeCode, label,
                "filetype.hint.office", "这类格式需要 Office 程序才能正确渲染。请导出后用 Office / WPS 打开。");
        }

        // ZIP 容器：docx / xlsx / pptx 也是 zip
        if (StartsWith(head, new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return extension switch
            {
                ".docx" or ".docm" => new FileKind("document", "docx", "Word 文档",
                    "filetype.hint.office", "Word 文档需要 Office 程序渲染。请导出后用 Word / WPS 打开。"),
                ".xlsx" or ".xlsm" => new FileKind("document", "xlsx", "Excel 表格",
                    "filetype.hint.office", "Excel 表格需要 Office 程序渲染。请导出后用 Excel / WPS 打开。"),
                ".pptx" or ".pptm" => new FileKind("document", "pptx", "PowerPoint 演示文稿",
                    "filetype.hint.office", "演示文稿需要 Office 程序渲染。请导出后用 PowerPoint / WPS 打开。"),
                ".apk" => new FileKind("archive", "apk", "Android 安装包", "filetype.hint.archive", "这只是一个压缩包，没有可显示的内容。"),
                ".jar" => new FileKind("archive", "jar", "Java 包", "filetype.hint.archive", "这只是一个压缩包，没有可显示的内容。"),
                _ => new FileKind("archive", "zip", "ZIP 压缩包",
                    "filetype.hint.archive", "压缩包没有可直接显示的内容。请导出后解压查看。"),
            };
        }

        // 压缩包
        if (StartsWith(head, "Rar!"u8))
        {
            return new FileKind("archive", "rar", "RAR 压缩包", "filetype.hint.archive", "压缩包没有可直接显示的内容。请导出后解压查看。");
        }

        if (StartsWith(head, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
        {
            return new FileKind("archive", "7z", "7z 压缩包", "filetype.hint.archive", "压缩包没有可直接显示的内容。请导出后解压查看。");
        }

        // 音视频
        if (head.Length >= 12 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70)
        {
            (string mediaCode, string label) = extension switch
            {
                ".mov" => ("mov", "QuickTime 视频"),
                ".m4a" => ("m4a", "M4A 音频"),
                ".heic" or ".heif" => ("heic", "HEIF 图片"),
                _ => ("mp4", "MP4 音视频"),
            };

            return new FileKind("media", mediaCode, label,
                "filetype.hint.media", "音视频不支持在窗口内受控预览——解码需要大块内存和专门组件。请导出后用播放器打开。");
        }

        if (StartsWith(head, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
        {
            return new FileKind("media", "mkv", "Matroska 音视频",
                "filetype.hint.media", "音视频不支持在窗口内受控预览。请导出后用播放器打开。");
        }

        if (StartsWith(head, "RIFF"u8) && head.Length >= 12 && head[8] == 0x41 && head[9] == 0x56 && head[10] == 0x49)
        {
            return new FileKind("media", "avi", "AVI 视频",
                "filetype.hint.media", "音视频不支持在窗口内受控预览。请导出后用播放器打开。");
        }

        if (StartsWith(head, "RIFF"u8) && head.Length >= 12 && head[8] == 0x57 && head[9] == 0x41 && head[10] == 0x56 && head[11] == 0x45)
        {
            return new FileKind("media", "wav", "WAV 音频",
                "filetype.hint.audio", "音频不支持在窗口内受控预览。请导出后用播放器打开。");
        }

        if (StartsWith(head, "fLaC"u8))
        {
            return new FileKind("media", "flac", "FLAC 音频",
                "filetype.hint.audio", "音频不支持在窗口内受控预览。请导出后用播放器打开。");
        }

        if (StartsWith(head, "ID3"u8) ||
            (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
        {
            return new FileKind("media", "mp3", "MP3 音频",
                "filetype.hint.audio", "音频不支持在窗口内受控预览。请导出后用播放器打开。");
        }

        // 可执行文件
        if (StartsWith(head, "MZ"u8))
        {
            return new FileKind("executable", "exe", "Windows 可执行文件",
                "filetype.hint.executable", "可执行文件不是用来查看的。请导出后确认来源再运行。");
        }

        if (StartsWith(head, new byte[] { 0x7F, 0x45, 0x4C, 0x46 }))
        {
            return new FileKind("executable", "elf", "Linux 可执行文件",
                "filetype.hint.executable", "可执行文件不是用来查看的。请导出后确认来源再运行。");
        }

        // 数据库
        if (StartsWith(head, "SQLite format 3"u8))
        {
            return new FileKind("archive", "sqlite", "SQLite 数据库",
                "filetype.hint.sqlite", "数据库文件需要专门的工具浏览。请导出后查看。");
        }

        return new FileKind("unknown", "unknown", "未知类型",
            "filetype.hint.unknown", "无法识别这个文件的类型，可能是二进制数据。请导出后用对应程序打开。");
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".ini", ".cfg",
        ".conf", ".yml", ".yaml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".c", ".cpp",
        ".h", ".hpp", ".py", ".java", ".go", ".rs", ".sh", ".bat", ".cmd", ".ps1", ".sql",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    /// <summary>按扩展名粗判：这个文件在我们的查看窗口里能被打开吗（文本或图片）。</summary>
    public static bool IsLikelyViewable(string? name)
    {
        return ExpectedCategory(name) is not null;
    }

    /// <summary>按扩展名推断"应该是什么"，推不出来返回 null。</summary>
    public static string? ExpectedCategory(string? name)
    {
        string extension = GetExtension(name);

        if (TextExtensions.Contains(extension))
        {
            return "text";
        }

        if (ImageExtensions.Contains(extension))
        {
            return "image";
        }

        return null;
    }

    private static string GetExtension(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetExtension(name).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        return data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
    }
}
