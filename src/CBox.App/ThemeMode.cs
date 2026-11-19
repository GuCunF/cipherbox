using System.IO;

namespace CBox.App;

/// <summary>
/// 浅色 / 深色。
///
/// 只做一件事：记住用户的选择，下次启动时决定要不要加载深色配色。
/// 切换需要重启——界面里全是 StaticResource，运行中换字典不会让已渲染的控件变色。
/// </summary>
internal static class AppTheme
{
    private static bool _loaded;
    private static bool _isDark;

    /// <summary>是否使用深色主题。</summary>
    public static bool IsDark
    {
        get
        {
            EnsureLoaded();
            return _isDark;
        }
        set
        {
            EnsureLoaded();
            _isDark = value;
            Save();
        }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherBox",
#if CBTEST
        "theme.test.txt"
#else
        "theme.txt"
#endif
    );

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                _isDark = File.ReadAllText(path).Trim() == "dark";
            }
        }
        catch
        {
            // 读不到就按浅色
        }
    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _isDark ? "dark" : "light");
        }
        catch
        {
            // 存不下就下次回浅色
        }
    }
}

