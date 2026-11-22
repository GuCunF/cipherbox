using Microsoft.Win32;

namespace CBox.App;

/// <summary>
/// .cbx 文件的关联与右键菜单。
///
/// 只写 HKCU（当前用户），好处：
///   1. 不需要管理员权限，点了就生效
///   2. 卸载/取消关联时不会影响别的用户
///   3. 系统里那套「默认程序」设置不会被强硬覆盖
///
/// 注册后：双击 .cbx → 打开本程序并进入解密页；右键 .cbx → 出现「用 CipherBox 解密」。
/// </summary>
internal static class FileAssociation
{
    private const string Extension = ".cbx";
    private const string ProgId = "CipherBox.cbx";

    private static string ClassesRoot => $@"Software\Classes";
    private static string ExtensionKey => $@"{ClassesRoot}\{Extension}";
    private static string ProgIdKey => $@"{ClassesRoot}\{ProgId}";

    /// <summary>右键菜单里「用 CipherBox 加密」那一项的动词名。</summary>
    private const string EncryptVerb = "CipherBoxEncrypt";

    /// <summary>当前是否由我们关联。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ExtensionKey);
            string? value = key?.GetValue(null) as string;
            return string.Equals(value, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>注册关联。返回是否成功。</summary>
    /// <summary>
    /// 用户是否主动关掉了右键菜单（记住这个选择，别每次启动又加回去）。
    /// </summary>
    private static string ContextMenuOffPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherBox",
#if CBTEST
        "no-context-menu.test.txt"
#else
        "no-context-menu.txt"
#endif
    );

    /// <summary>
    /// 确保「用 CipherBox 加密」右键菜单存在。
    /// 幂等：已存在就覆写同样的值；用户主动关过就跳过。
    /// 只动这一项，不碰 .cbx 文件关联。
    /// </summary>
    public static void EnsureContextMenu()
    {
        try
        {
            if (System.IO.File.Exists(ContextMenuOffPath))
            {
                return;
            }

            string? exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            string label = CBox.Core.Localization.GetRaw("assoc.encrypt.menu") ?? "Encrypt with CipherBox";

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\*\shell\{EncryptVerb}"))
            {
                key.SetValue(null, label);
                key.SetValue("Icon", $"\"{exe}\"");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\*\shell\{EncryptVerb}\command"))
            {
                key.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            NotifyShell();
            CBox.Core.CbxLog.Info("右键菜单已确保存在");
        }
        catch (Exception ex)
        {
            CBox.Core.CbxLog.Warn($"注册右键菜单失败：{ex.Message}");
        }
    }

    /// <summary>记住「用户不要右键菜单」，并把它删掉。</summary>
    private static void RememberContextMenuOff()
    {
        try
        {
            string path = ContextMenuOffPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "off");
        }
        catch
        {
            // 记不住就算了，下次启动会再加回来
        }
    }

    public static bool Register()
    {
        try
        {
            System.IO.File.Delete(ContextMenuOffPath);
        }
        catch { }

        try
        {
            string? exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }

            // 1) 扩展名 → 我们的 ProgId
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ExtensionKey))
            {
                key.SetValue(null, ProgId);
            }

            // 2) ProgId 的显示名与图标
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ProgIdKey))
            {
                key.SetValue(null, "CipherBox 加密文件");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ProgIdKey}\DefaultIcon"))
            {
                key.SetValue(null, $"\"{exe}\",0");
            }

            // 3) 双击打开 = 解密（"open" 是 Shell 的默认动词）
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ProgIdKey}\shell\open"))
            {
                key.SetValue(null, "用 CipherBox 解密");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ProgIdKey}\shell\open\command"))
            {
                key.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            // 4) 右键再给一个「只读查看」，不解出文件
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ProgIdKey}\shell\view"))
            {
                key.SetValue(null, "用 CipherBox 查看（只读）");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ProgIdKey}\shell\view\command"))
            {
                key.SetValue(null, $"\"{exe}\" --view \"%1\"");
            }

            // 4) 任意文件的右键菜单：「用 CipherBox 加密」
            //    这是 HKCU\Software\Classes\*\shell，只影响当前用户
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\*\shell\{EncryptVerb}"))
            {
                key.SetValue(null, CBox.Core.Localization.GetRaw("assoc.encrypt.menu") ?? "Encrypt with CipherBox");
                key.SetValue("Icon", $"\"{exe}\"");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\*\shell\{EncryptVerb}\command"))
            {
                key.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            NotifyShell();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>取消关联。返回是否成功。</summary>
    public static bool Unregister()
    {
        try
        {
            // 只删「指向我们的」那个值，别把用户自己设的关联也抹掉
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(ExtensionKey, writable: true))
            {
                if (key is not null)
                {
                    if (string.Equals(key.GetValue(null) as string, ProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        key.DeleteValue(null, throwOnMissingValue: false);
                    }
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\*\shell\{EncryptVerb}", throwOnMissingSubKey: false);

            Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false);

            NotifyShell();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 告诉资源管理器「关联变了」。
    /// 不通知的话，双击图标可能还是旧的，要等它自己刷新。
    /// </summary>
    private static void NotifyShell()
    {
        try
        {
            NativeMethods.SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 通知失败不影响关联本身
        }
    }
}
