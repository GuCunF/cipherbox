using System.Reflection;

namespace CBox.Core;

/// <summary>
/// 版本号。唯一来源是仓库根目录的 Directory.Build.props。
///
/// 规则（2026-11-16 起）：
///   - **测试版四段**：2.0.0.7（主.次.功能.补丁），补丁号用来分辨"手上这个 exe 是不是最新的"
///   - **发布版三段**：2.0.0（去掉补丁位），对外只露三段
///
/// 两套都是从同一份 Directory.Build.props 出发：
/// 发布打包脚本会用 -p:Version=2.0.0 覆盖成三段，程序按实际版本位数显示。
/// </summary>
public static class CbxVersion
{
    public static string Current { get; } = Resolve();

    /// <summary>拼给人看的完整串，例如 "1.3.0.1"。</summary>
    private static string Resolve()
    {
        try
        {
            Version? version = typeof(CbxVersion).Assembly.GetName().Version;
            if (version is null)
            {
                return "未知";
            }

            // 发布版把第四段置零（打包时 -p:Version=2.0.0 会让 Revision 为 0），
            // 这里据此决定显示三段还是四段——避免"2.0.0.0"这种对外难看的写法
            if (version.Revision == 0)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }

            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>带程序名前缀，用于窗口标题、横幅、日志。</summary>
    public static string Display => $"CipherBox {Current}";
}
