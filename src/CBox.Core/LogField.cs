namespace CBox.Core;

/// <summary>
/// 日志字段集合。用位标志表示"这次要记录哪些内容"。
///
/// 设计见 logs\10-日志与隐私设计.md：
///   - 默认集（DefaultSet）只含不涉及加密信息的最小集合
///   - 敏感集（SensitiveMask）永远进不了默认集，且不写入配置文件
/// </summary>
[Flags]
public enum LogField
{
    None = 0,

    // ---- 基础：默认开 ----
    Operation = 1 << 0,   // 做了什么事（加密/解密/查看）
    Result = 1 << 1,     // 成功还是失败
    Duration = 1 << 2,   // 耗时
    DataSize = 1 << 3,   // 字节数
    ErrorType = 1 << 4,  // 错误类型与简短消息

    // ---- 扩展：敏感，默认关 ----
    ErrorStack = 1 << 5,  // 异常堆栈（可能带路径）
    Paths = 1 << 6,      // 完整路径
    FileName = 1 << 7,   // 原始文件名
    Acl = 1 << 8,        // 目录 ACL / 属性
    Snapshot = 1 << 9,   // 环境快照（用户名、磁盘、TEMP）
    Progress = 1 << 10,  // 分块进度
}

public static class LogFields
{
    /// <summary>默认开启的集合：不泄露"你在处理什么文件"。</summary>
    public const LogField DefaultSet =
        LogField.Operation | LogField.Result | LogField.Duration |
        LogField.DataSize | LogField.ErrorType;

    /// <summary>敏感集合：无论怎么配置都不能进入默认集，也不写入配置文件。</summary>
    public const LogField SensitiveMask =
        LogField.ErrorStack | LogField.Paths | LogField.FileName |
        LogField.Acl | LogField.Snapshot | LogField.Progress;
}
