using System.Runtime.InteropServices;

namespace CBox.App;

/// <summary>
/// 极少量 Windows API。放在单独文件里，方便一眼看清「我们到底调了系统什么」。
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// 通知资源管理器「文件关联变了」。
    /// 不调的话，注册完双击 .cbx 可能仍按旧的关联走，要等系统自己刷新。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
