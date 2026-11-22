using System.IO;

namespace CBox.App;

/// <summary>
/// 最近打开过的文件（最多 5 条）。
///
/// 只记路径，不记任何内容或口令。存在
/// %LOCALAPPDATA%\CipherBox\recent.txt，一行一条，最新的在最前面。
///
/// 界面上不占额外位置：挂在「浏览…」按钮旁边的小下拉里。
/// </summary>
internal static class RecentFiles
{
    private const int MaxEntries = 5;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherBox",
#if CBTEST
        "recent.test.txt"
#else
        "recent.txt"
#endif
    );

    /// <summary>读取全部记录（已过滤掉不存在的文件）。</summary>
    public static List<string> Load()
    {
        var result = new List<string>();

        try
        {
            if (!File.Exists(StorePath))
            {
                return result;
            }

            foreach (string line in File.ReadAllLines(StorePath))
            {
                string path = line.Trim();

                if (path.Length > 0 && File.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(path);
                }

                if (result.Count >= MaxEntries)
                {
                    break;
                }
            }
        }
        catch
        {
            // 读不到就当没有
        }

        return result;
    }

    /// <summary>把某个路径提到最前面（去重、截断）。</summary>
    public static void Touch(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var entries = Load();

            entries.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, path);

            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            string directory = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllLines(StorePath, entries);
        }
        catch
        {
            // 存不下就下次再说
        }
    }
}
