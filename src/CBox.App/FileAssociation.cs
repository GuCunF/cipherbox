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
    public static bool Register()
    {
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
