using System.IO;

namespace CBox.App;

/// <summary>
/// 批量加密的执行逻辑。
///
/// 与「整个文件夹」的区别：
///   - 这里处理的是用户**手动挑的一批文件**，它们可能来自不同目录
///   - 输出到同一个目录，每个文件各出一个 .cbx
///
/// 安全提醒是硬要求：同一批文件共用一个口令，
/// 口令一泄露就是整批暴露，所以进入循环前必须让用户确认。
/// </summary>
internal static class BatchEncrypt
{
    /// <summary>
    /// 给一批文件算出各自的输出路径（都在同一个目录下）。
    /// </summary>
    public static string[] PlanTargets(string[] sources, string outputDirectory)
    {
        var targets = new string[sources.Length];

        for (int i = 0; i < sources.Length; i++)
        {
            string name = Path.GetFileName(sources[i]) + ".cbx";
            targets[i] = Path.Combine(outputDirectory, name);
        }

        return targets;
    }

    /// <summary>过滤掉不存在或重复的路径，保持输入顺序。</summary>
    public static string[] Normalize(string[] paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(paths.Length);

        foreach (string path in paths)
        {
            string full;

            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (File.Exists(full) && seen.Add(full))
            {
                result.Add(full);
            }
        }

        return result.ToArray();
    }
}
