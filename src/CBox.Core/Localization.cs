using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CBox.Core;

/// <summary>
/// 界面文字的查找表。
///
/// 每种语言一份 JSON（内嵌在程序里），格式是扁平的 key → 文本：
///   "enc.start": "开始加密"
///
/// 缺词时**回退到中文并记 WARN**，界面会显示成「!key!」——
/// 这比默默显示空白好，测试阶段一眼就能发现漏翻译。
///
/// 新增一种语言：复制 strings.zh-CN.json 改名为 strings.xx-XX.json，
/// 在 csproj 里已经用通配符包含，不需要改代码。
/// </summary>
public static class Localization
{
    private const string FallbackLanguage = "zh-CN";

    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _fallback = new();

    public static string Language { get; private set; } = FallbackLanguage;

    /// <summary>可用语言列表（从内嵌资源名推出来，比如 zh-CN、en-US）。</summary>
    public static IReadOnlyList<string> AvailableLanguages { get; } = Discover();

    public static void Initialize()
    {
        _fallback = Load(FallbackLanguage);

        // 优先级：环境变量 CBOX_LANG（测试/脚本） > 用户保存的选择 > 系统语言 > 中文
        string? overrideLanguage = Environment.GetEnvironmentVariable("CBOX_LANG");
        if (!string.IsNullOrWhiteSpace(overrideLanguage) && AvailableLanguages.Contains(overrideLanguage))
        {
            SetLanguage(overrideLanguage);
            CbxLog.Info($"界面语言由 CBOX_LANG 指定：{overrideLanguage}");
            return;
        }

        string? saved = LoadSavedLanguage();
        if (saved is not null)
        {
            SetLanguage(saved);
            CbxLog.Info($"界面语言来自用户设置：{saved}");
            return;
        }

        string system = CultureInfo.CurrentUICulture.Name;
        string pick = AvailableLanguages.Contains(system) ? system : FallbackLanguage;

        SetLanguage(pick);
    }

    /// <summary>用户选择的语言存这里。读不到就当没设置过。</summary>
    private static string PreferencesFile
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CipherBox");

            return Path.Combine(directory, "language.txt");
        }
    }

    public static string? LoadSavedLanguage()
    {
        try
        {
            string path = PreferencesFile;
            if (!File.Exists(path))
            {
                return null;
            }

            string language = File.ReadAllText(path).Trim();
            return AvailableLanguages.Contains(language) ? language : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveLanguage(string language)
    {
        try
        {
            string path = PreferencesFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, language);
        }
        catch (Exception ex)
        {
            // 存不下也无所谓，下次启动会回到跟随系统
            CbxLog.Warn($"保存语言设置失败：{ex.Message}");
        }
    }

    public static void SetLanguage(string language)
    {
        if (!AvailableLanguages.Contains(language))
        {
            language = FallbackLanguage;
        }

        _current = language == FallbackLanguage ? _fallback : Load(language);
        Language = language;
    }

    /// <summary>
    /// 取词条但不记"缺少词条"日志。
    /// 用于探测可选的 *.pro 变体——没有 pro 版本是正常情况，不该刷警告。
    /// </summary>
    public static string? TryGetRaw(string key)
    {
        return _current.TryGetValue(key, out string? value) ? value : null;
    }

    public static string? GetRaw(string key)
    {
        if (_current.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (_fallback.TryGetValue(key, out string? fallback))
        {
            if (Language != FallbackLanguage)
            {
                CbxLog.Warn($"缺少 {Language} 词条：{key}（已回退中文）");
            }

            return fallback;
        }

        CbxLog.Warn($"缺少词条：{key}");
        return null;
    }

    private static Dictionary<string, string> Load(string language)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var assembly = typeof(Localization).Assembly;
            string resourceName = $"CBox.Core.Strings.strings-{language}.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                // 把实际的资源名列出来，否则"找不到语言资源"根本没法排查
                CbxLog.Warn($"找不到语言资源：{resourceName}");
                CbxLog.Warn($"实际内嵌资源：{string.Join(", ", assembly.GetManifestResourceNames())}");
                return map;
            }

            using var reader = new StreamReader(stream);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());

            if (parsed is not null)
            {
                foreach (var pair in parsed)
                {
                    map[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            CbxLog.Error($"加载语言包失败：{language}", ex);
        }

        return map;
    }

    private static IReadOnlyList<string> Discover()
    {
        var languages = new List<string>();
        var assembly = typeof(Localization).Assembly;

        foreach (string name in assembly.GetManifestResourceNames())
        {
            const string prefix = "CBox.Core.Strings.strings-";
            const string suffix = ".json";

            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                languages.Add(name[prefix.Length..^suffix.Length]);
            }
        }

        if (languages.Count == 0)
        {
            languages.Add(FallbackLanguage);
        }

        return languages;
    }
}

