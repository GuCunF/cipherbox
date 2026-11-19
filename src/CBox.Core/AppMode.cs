namespace CBox.Core;

/// <summary>
/// 一般模式 / 专业模式。
///
/// 影响两件事：
///   1. 界面措辞（一般说"怎么用"，专业说"具体参数"）
///   2. 专业选项的可见性（全加密、不做后缀名检查、加密文件名、日志完整路径）
///
/// 选择会持久化到 %LOCALAPPDATA%\CipherBox\mode.txt，重启后保持。
/// </summary>
public static class AppMode
{
    private static bool _loaded;

    /// <summary>是否专业模式。默认一般模式。</summary>
    public static bool IsProfessional { get; set; }

    /// <summary>
    /// 用户是否已经明确选过模式。
    /// 用来判断"要不要弹首次引导"——没选过就先问一句，别一上来就摆一堆参数。
    /// </summary>
    public static bool HasChosen { get; private set; }

    /// <summary>
    /// 测试版与发布版各用各的设置文件。
    /// 共用一份会出现"测试版开了专业模式，发布版也跟着开"——发布版默认必须是关的。
    /// </summary>
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherBox",
#if CBTEST
        "mode.test.txt"
#else
        "mode.txt"
#endif
    );

    /// <summary>启动时读一次；读不到就是一般模式。</summary>
    public static void Load()
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
                IsProfessional = File.ReadAllText(path).Trim() == "professional";
                HasChosen = true;
            }
        }
        catch
        {
            // 读不到就用默认
        }
    }

    public static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, IsProfessional ? "professional" : "standard");
            HasChosen = true;
        }
        catch
        {
            // 存不下也无所谓，下次回到一般模式
        }
    }
}
