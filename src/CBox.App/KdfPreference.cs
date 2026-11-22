using System.IO;

namespace CBox.App;

/// <summary>
/// 界面上的密钥派生选择。
///
/// 两套口径：
///   - 一般模式：固定 Argon2id，界面上不出现选择框（用户不需要操心）
///   - 专业模式：可以切回 PBKDF2，用于兼容或对比测试
///
/// 选择会记在设置里，下次启动沿用。
/// </summary>
internal static class KdfPreference
{
    /// <summary>Argon2id 的 KDF 编号。</summary>
    public const byte Argon2id = 2;

    /// <summary>PBKDF2-SHA256 的 KDF 编号。</summary>
    public const byte Pbkdf2 = 1;

    private static bool _loaded;
    private static byte _selected = Argon2id;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherBox",
#if CBTEST
        "kdf.test.txt"
#else
        "kdf.txt"
#endif
    );

    /// <summary>当前选中的 KDF 编号。</summary>
    public static byte Selected
    {
        get
        {
            EnsureLoaded();
            return _selected;
        }
        set
        {
            EnsureLoaded();
            _selected = value is Argon2id or Pbkdf2 ? value : Argon2id;
            Save();
        }
    }

    /// <summary>
    /// 实际要用的 KDF：一般模式不看设置，直接用 Argon2id。
    /// </summary>
    public static byte Effective(bool professional) => professional ? Selected : Argon2id;

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

                if (byte.TryParse(text, out byte value) && value is Argon2id or Pbkdf2)
                {
                    _selected = value;
                }
            }
        }
        catch
        {
            // 读不到就用默认
        }
    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _selected.ToString());
        }
        catch
        {
            // 存不下就下次回默认
        }
    }
}
