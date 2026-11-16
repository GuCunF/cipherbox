namespace CBox.Core;

/// <summary>
/// 一个环境问题。
///
/// 分两套说法：
///   Summary / Advice —— 给普通用户看的，一句话说清"出什么事了"和"该怎么办"
///   Technical        —— 给排查问题的人看的，含完整路径与异常信息
/// </summary>
public sealed record EnvironmentIssue(
    string Area,
    string Summary,
    string Advice,
    string Technical);

/// <summary>
/// 启动时的环境自检。
///
/// 目的很具体：把"运行时才发现写不进去"提前到启动时。
/// 2026-11-15 那次排查花了好几个小时，根因是程序跑在受限环境里、只有工作区可写；
/// 如果启动时探一下目录可写性，五秒就能定位。
///
/// 原则：**静默检测，只在发现问题时弹一次窗**，说明要短。
/// </summary>
public static class CbxEnvironmentProbe
{
    /// <summary>磁盘剩余低于这个值就提醒（加解密会产出同等大小的文件）。</summary>
    private const long LowDiskBytes = 500L * 1024 * 1024;

    public static IReadOnlyList<EnvironmentIssue> Run()
    {
        var issues = new List<EnvironmentIssue>();

        string temp = Path.GetTempPath();
        string appDirectory = AppContext.BaseDirectory;

        CheckWritable(temp, UiText.Get("env.temp"), issues);
        CheckWritable(appDirectory, UiText.Get("env.appdir"), issues);
        CheckUserDataDirectory(issues);
        CheckFreeSpace(temp, issues);

        return issues;
    }

    private static void CheckWritable(string directory, string label, List<EnvironmentIssue> issues)
    {
        try
        {
            string probe = Path.Combine(directory, $".cbox_env_probe_{Guid.NewGuid():N}.tmp");

            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex)
        {
            issues.Add(new EnvironmentIssue(
                label,
                UiText.Get("env.cannotwrite", label),
                UiText.Get("env.advice.move"),
                $"路径：{directory}{Environment.NewLine}" +
                $"异常：{ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                $"HResult：0x{ex.HResult:X8}"));
        }
    }

    /// <summary>
    /// 探测 %LOCALAPPDATA%：它在正常的用户环境里永远可写，
    /// 写不进去基本只有一种解释——程序跑在受限容器里，只能写特定目录。
    /// 这正是 2026-11-15 那次几小时排查的场景。
    /// </summary>
    private static void CheckUserDataDirectory(List<EnvironmentIssue> issues)
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(userData))
        {
            return;
        }

        try
        {
            string probe = Path.Combine(userData, $".cbox_env_probe_{Guid.NewGuid():N}.tmp");

            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex)
        {
            issues.Add(new EnvironmentIssue(
                UiText.Get("env.runtime"),
                UiText.Get("env.runtime.summary"),
                UiText.Get("env.runtime.advice"),
                $"无法写入用户数据目录：{userData}{Environment.NewLine}" +
                $"异常：{ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                $"HResult：0x{ex.HResult:X8}{Environment.NewLine}" +
                UiText.Get("env.runtime.cause")));
        }
    }

    private static void CheckFreeSpace(string directory, List<EnvironmentIssue> issues)
    {
        try
        {
            string? root = Path.GetPathRoot(directory);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < LowDiskBytes)
            {
                long megabytes = drive.AvailableFreeSpace / 1024 / 1024;

                issues.Add(new EnvironmentIssue(
                UiText.Get("env.disk"),
                UiText.Get("env.disk.summary", drive.Name, megabytes),
                UiText.Get("env.disk.advice"),
                    $"磁盘：{drive.Name}{Environment.NewLine}" +
                    $"可用：{drive.AvailableFreeSpace:N0} 字节{Environment.NewLine}" +
                    $"总计：{drive.TotalSize:N0} 字节"));
            }
        }
        catch
        {
            // 查不到就算了，不是致命问题
        }
    }
}