using System.Diagnostics;

namespace CBox.Core;

/// <summary>
/// 测试用的钩子。
///
/// 分成两部分：
///   - <see cref="KeepFailedOutput"/>：**真实功能**（专业模式下的“失败时保留半成品”），
///     两种构建都有，不受 CBTEST 影响。
///   - 其余（模拟慢速、故障注入）：**只存在于测试构建**。发布构建里连方法体都不编译，
///     只留同名空方法让调用点无需改动——这样发布产物里搜不到任何测试代码。
/// </summary>
public static class CbxTestHooks
{
    /// <summary>
    /// 失败时是否保留写了一半的临时文件。
    /// 正式版默认 false（失败即销毁）；专业模式可临时打开便于排查。
    /// </summary>
    public static bool KeepFailedOutput { get; set; }

#if CBTEST
    /// <summary>是否开启“模拟慢速”。关闭时不产生任何额外开销。</summary>
    public static bool SlowMode { get; set; }

    /// <summary>每完成百分之多少的工作停顿一次。</summary>
    public static int SlowEveryPercent { get; set; } = 10;

    /// <summary>每次停顿多少毫秒。</summary>
    public static int SlowDelayMs { get; set; } = 800;

    /// <summary>加密进行到百分之多少时抛出模拟异常；-1 表示不注入。</summary>
    public static int FailEncryptAtPercent { get; set; } = -1;

    /// <summary>解密进行到百分之多少时抛出模拟异常；-1 表示不注入。</summary>
    public static int FailDecryptAtPercent { get; set; } = -1;

    /// <summary>
    /// 在处理第 <paramref name="done"/> 块**之前**调用。
    /// 块数少时（≤10 块）每块都停顿，否则每 10% 停顿一次——小文件也要看得见。
    /// </summary>
    public static void Throttle(int done, int total)
    {
        if (!SlowMode || total <= 0)
        {
            return;
        }

        int step = total <= 10
            ? 1
            : Math.Max(1, (int)Math.Ceiling(total * SlowEveryPercent / 100.0));

        if (done % step == 0)
        {
            Thread.Sleep(SlowDelayMs);
        }
    }

    /// <summary>到达设定百分比对应的块号就抛异常，模拟加密写盘失败。</summary>
    public static void MaybeFailEncrypt(int done, int total)
    {
        if (ShouldFail(FailEncryptAtPercent, done, total))
        {
            FailEncryptAtPercent = -1;   // 只失败一次
            throw new IOException($"模拟磁盘写入失败（加密，第 {done}/{total} 块）");
        }
    }

    /// <summary>到达设定百分比对应的块号就抛异常，模拟解密写盘失败。</summary>
    public static void MaybeFailDecrypt(int done, int total)
    {
        if (ShouldFail(FailDecryptAtPercent, done, total))
        {
            FailDecryptAtPercent = -1;   // 只失败一次
            throw new IOException($"模拟磁盘写入失败（解密，第 {done}/{total} 块）");
        }
    }

    private static bool ShouldFail(int percent, int done, int total)
    {
        if (percent < 0 || total <= 0)
        {
            return false;
        }

        int atChunk = (int)((long)total * Math.Clamp(percent, 0, 100) / 100);
        atChunk = Math.Clamp(atChunk, 0, total - 1);

        return done >= atChunk;
    }
#else
    // 发布构建：三个方法保留成空实现，调用点无需任何条件编译
    public static void Throttle(int done, int total)
    {
    }

    public static void MaybeFailEncrypt(int done, int total)
    {
    }

    public static void MaybeFailDecrypt(int done, int total)
    {
    }
#endif
}
