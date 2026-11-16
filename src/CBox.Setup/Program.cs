using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace CBox.Setup;

/// <summary>
/// CipherBox 安装器。
///
/// 设计原则：
///   1. **按当前用户安装**，不申请管理员权限（写 %LOCALAPPDATA%，不碰 Program Files）
///   2. 不联网、不下载、不写系统级注册表
///   3. 只做四件事：解压、建快捷方式、写卸载项、可完整卸载
///
/// 用法：
///   CipherBoxSetup.exe                    图形安装
///   CipherBoxSetup.exe --silent           静默安装（测试用）
///   CipherBoxSetup.exe --silent --dir X   安装到指定目录（测试用）
///   CipherBoxSetup.exe --uninstall        卸载
/// </summary>
internal static class Program
{
    private const string AppName = "CipherBox";
    private const string PayloadName = "payload.zip";
    private const string SetupFileName = "CipherBoxSetup.exe";

    private static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    private static string UninstallKey => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            bool silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            SilentMode = silent;
            _logPath = ReadOption(args, "--log");
            Log($"启动：mode={(silent ? "silent" : "gui")} args={string.Join(' ', args)}");
            bool uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
            string? dir = ReadOption(args, "--dir");

            return uninstall
                ? Uninstall(silent)
                : Install(silent, dir);
        }
        catch (Exception ex)
        {
            Error($"安装/卸载失败：{ex.Message}");
            return 1;
        }
    }

    // ---------------- 安装 ----------------

    private static int Install(bool silent, string? targetDir)
    {
        string target = string.IsNullOrWhiteSpace(targetDir) ? DefaultInstallDir : targetDir!;

        if (!silent && MessageBox.Show(
                $"将把 {AppName} 安装到：\n{target}\n\n继续吗？",
                $"{AppName} 安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
            != DialogResult.OK)
        {
            return 0;
        }

        // 1. 解压（先清掉旧的程序文件，保留用户数据目录不受影响）
        Directory.CreateDirectory(target);
        ExtractPayload(target);

        // 2. 把安装器自己复制进去，卸载时要用
        string self = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        string installedSetup = Path.Combine(target, SetupFileName);

        if (!string.Equals(self, installedSetup, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, installedSetup, overwrite: true);
        }

        string exePath = Path.Combine(target, "CipherBox.exe");

        if (!File.Exists(exePath))
        {
            Error("安装包里没有找到 CipherBox.exe，请重新下载。");
            return 1;
        }

        // 3. 快捷方式：桌面 + 开始菜单
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"), exePath);

        try
        {
            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, $"{AppName}.lnk"), exePath);
            CreateShortcut(Path.Combine(startMenu, "卸载.lnk"), installedSetup, "--uninstall");
        }
        catch (Exception ex)
        {
            // 开始菜单被策略限制时不该让安装失败
            Log($"开始菜单跳过：{ex.Message}");
        }

        // 4. 卸载项（当前用户，不需要管理员）
        string version = FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "0.0.0.0";

        try
        {
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("DisplayIcon", exePath);
            key.SetValue("InstallLocation", target);
            key.SetValue("Publisher", "CipherBox contributors");
            key.SetValue("UninstallString", $"\"{installedSetup}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            // 估算体积（KB），让"应用和功能"里显示正常
            key.SetValue("EstimatedSize", DirectorySizeKb(target), RegistryValueKind.DWord);
        }
        }
        catch (Exception ex)
        {
            Log($"卸载项写入跳过：{ex.Message}");
        }

        if (!silent)
        {
            MessageBox.Show(
                $"{AppName} 安装完成。\n\n安装位置：\n{target}\n\n桌面和开始菜单都有快捷方式。\n卸载：开始菜单里的「卸载」，或 Windows 设置 → 应用。",
                "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }

    // ---------------- 卸载 ----------------

    private static int Uninstall(bool silent)
    {
        string target = DefaultInstallDir;

        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            if (key?.GetValue("InstallLocation") is string saved && !string.IsNullOrWhiteSpace(saved))
            {
                target = saved;
            }
        }

        if (!silent && MessageBox.Show(
                $"将卸载 {AppName}，并删除：\n{target}\n\n注意：不会删除你自己加密的 .cbx 文件。\n继续吗？",
                $"{AppName} 卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
            != DialogResult.OK)
        {
            return 0;
        }

        // 快捷方式
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"));

        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

        if (Directory.Exists(startMenu))
        {
            TryDeleteDirectory(startMenu);
        }

        // 安装目录：先删文件，再删目录（正在运行的 exe 可能占用）
        if (Directory.Exists(target))
        {
            TryDeleteDirectory(target);
        }

        // 卸载项
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (!silent)
        {
            MessageBox.Show($"{AppName} 已卸载。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }

    // ---------------- 工具 ----------------

    private static void ExtractPayload(string target)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName);

        if (payload is null)
        {
            throw new InvalidOperationException("安装包缺少载荷（payload.zip）。");
        }

        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;   // 目录条目
            }

            string destination = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    /// <summary>用 WScript.Shell 建 .lnk，避免引入第三方库。</summary>
    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments = "")
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");

        if (shellType is null)
        {
            return;   // 建不了快捷方式不算致命
        }

        try
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = targetPath;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            link.Description = AppName;
            link.Save();
        }
        catch (Exception ex)
        {
            // 建不了快捷方式不该让安装失败（比如桌面被策略重定向/只读）
            Log($"快捷方式跳过：{shortcutPath} - {ex.Message}");
        }
    }

    private static int DirectorySizeKb(string path)
    {
        try
        {
            long bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);

            return (int)Math.Min(bytes / 1024, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 文件被占用时忽略，用户可稍后手动删
        }
    }

    /// <summary>静默模式绝不能弹窗——否则会卡住无人值守安装。</summary>
    private static bool SilentMode { get; set; }

    private static void Error(string message)
    {
        Log("ERROR: " + message);

        if (!SilentMode)
        {
            MessageBox.Show(message, $"{AppName} 安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>把过程写进日志，静默安装时用来排查。</summary>
    private static string? _logPath;

    private static void Log(string message)
    {
        try
        {
            string path = _logPath ?? Path.Combine(Path.GetTempPath(), "cipherbox-setup.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去不影响安装
        }
    }
}
