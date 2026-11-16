using System.Globalization;

namespace CBox.Core;

/// <summary>
/// 界面文字的统一入口。
///
/// 规则：
///   1. 面向用户的文字一律走这里，不写中文字面量
///   2. 需要区分一般/专业时，在 JSON 里加 ".pro" 后缀的条目，调用点不写 if/else
///   3. 句子用 {0} 占位，不要字符串拼接（否则其他语言没法调语序）
///   4. 缺词会显示成「!key!」，测试阶段一眼可见
/// </summary>
public static class UiText
{
    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>首次取词时自动加载语言表，命令行程序不需要显式初始化。</summary>
    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitGate)
        {
            if (_initialized)
            {
                return;
            }

            Localization.Initialize();
            _initialized = true;
        }
    }

    /// <summary>取词条。专业模式下自动优先取 "key.pro"。</summary>
    public static string Get(string key, params object?[] args)
    {
        EnsureInitialized();

        string? text = null;

        if (AppMode.IsProfessional)
        {
            // 没有 pro 变体是常态，用静默探测，避免日志里刷"缺少词条"
            text = Localization.TryGetRaw(key + ".pro");
        }

        text ??= Localization.GetRaw(key);

        if (text is null)
        {
            return $"!{key}!";
        }

        if (args.Length == 0)
        {
            return text;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException ex)
        {
            CbxLog.Warn($"词条 {key} 的占位符不匹配：{ex.Message}");
            return text;
        }
    }

    /// <summary>
    /// 取词条，缺失时用调用方给的中文兜底（不显示 !key!）。
    /// 用于 Core 层那些"可能还没翻译"的术语，比如文件类型名。
    /// </summary>
    public static string GetOrFallback(string key, string fallback, string? arg = null)
    {
        EnsureInitialized();

        string? text = Localization.TryGetRaw(key);

        if (text is null)
        {
            return arg is null ? fallback : string.Format(CultureInfo.CurrentCulture, fallback, arg);
        }

        if (arg is null)
        {
            return text;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, arg);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    /// <summary>两个版本直接写在调用点的写法，保留给还没迁到资源表的零散文案。</summary>
    public static string Pick(string general, string professional)
    {
        return AppMode.IsProfessional ? professional : general;
    }
}
