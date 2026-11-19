using System.IO;
using Microsoft.Win32;

namespace CBox.App;

/// <summary>
/// 界面配色：浅色 / 深色 / 跟随系统。
///
/// 只做一件事：决定启动时要不要把深色配色字典叠加上去。
/// 切换后需要重启——界面里用了 StaticResource 的地方在窗口构造时就固化了。
/// </summary>
internal static class AppTheme
{
    /// <summary>用户选择：light / dark / system。默认 system。</summary>
    private static string _choice = "system";

    private static bool _loaded;

    /// <summary>最终是否用深色（已把「跟随系统」解析成具体值）。</summary>
    public static bool IsDark
    {
        get
        {
            EnsureLoaded();

            return _choice switch
            {
                "dark" => true,
                "light" => false,
                _ => SystemPrefersDark(),
            };
        }
    }

    /// <summary>当前选择（原样，可能是 system）。</summary>
    public static string Choice
    {
        get
        {
            EnsureLoaded();
            return _choice;
        }
        set
        {
            EnsureLoaded();
            _choice = value;
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
                string text = File.ReadAllText(path).Trim();

                // 兼容旧值：以前只存 light/dark
                if (text is "light" or "dark" or "system")
                {
                    _choice = text;
                }
            }
        }
        catch
        {
            // 读不到就跟随系统
        }
    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _choice);
        }
        catch
        {
            // 存不下就下次重新跟随系统
        }
    }

    /// <summary>
    /// 读 Windows 的「应用模式」设置。
    /// 键值：AppsUseLightTheme = 1 表示浅色，0 表示深色；键不存在时按浅色处理。
    /// </summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            object? value = key?.GetValue("AppsUseLightTheme");

            if (value is int light)
            {
                return light == 0;
            }
        }
        catch
        {
            // 读不到就浅色
        }

        return false;
    }
}