using System.Text;

namespace CBox.Core;

/// <summary>
/// 诊断日志。默认关闭，测试阶段由界面上的开关打开。
///
/// 永远不记录：口令、密钥、明文内容。
/// 会记录：时间、级别、完整路径、文件属性、磁盘空间、耗时、异常类型/HResult/堆栈。
///
/// 开启日志等于把"你在处理哪些文件"写进本机文件，请知悉这一点。
/// </summary>
public static class CbxLog
{
    private static readonly object Gate = new();
    private static string? _logDirectory;
    private static bool _sessionHeaderWritten;
    private static LogField _fields = LogField.None;

    /// <summary>
    /// 当前启用的日志字段。
    /// 敏感位只能通过 <see cref="SetSensitive"/> 临时打开，且不会被保存。
    /// </summary>
    public static LogField Fields
    {
        get => _fields;
        private set => _fields = value;
    }

    /// <summary>是否记录任何日志。true = 默认集；false = 全关。</summary>
    public static bool Enabled
    {
        get => _fields != LogField.None;
        set => _fields = value ? LogFields.DefaultSet : LogField.None;
    }

    /// <summary>是否有超出默认集的字段被打开（用于界面提示）。O(1) 判断。</summary>
    public static bool HasExtraFields => (_fields & ~LogFields.DefaultSet) != 0;

    /// <summary>打开/关闭一个敏感字段。只有这里能碰到敏感位。</summary>
    public static void SetSensitive(LogField field, bool enabled)
    {
        if ((field & LogFields.SensitiveMask) == 0)
        {
            return;   // 非敏感字段走 Enabled，不从这里进
        }

        _fields = enabled ? _fields | field : _fields & ~field;
    }

    public static bool Has(LogField field) => (_fields & field) == field;

    /// <summary>
    /// 调试构建专用：日志完全放开，连路径、文件名、堆栈、环境快照都记。
    ///
    /// 只在 CBTEST 构建里调用；发布构建没有这个入口，敏感字段永远进不来。
    /// 无论哪种构建，口令、密钥、明文内容都不记录——那不是开关问题，是根本没有这条代码路径。
    /// </summary>
    public static void UseDebugDefaults()
    {
#if CBTEST
        _fields = LogField.Operation | LogField.Result | LogField.Duration | LogField.DataSize |
                  LogField.ErrorType | LogField.ErrorStack | LogField.Paths | LogField.FileName |
                  LogField.Acl | LogField.Snapshot | LogField.Progress;
#else
        _fields = LogFields.DefaultSet;
#endif
    }

    /// <summary>
    /// 路径脱敏。没开「完整路径」时只留一个短哈希——
    /// 既能对上"是不是同一个文件"，又不泄露你在处理什么。
    /// </summary>
    public static string SafePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (Has(LogField.Paths))
        {
            return path;
        }

        return $"‹path:{ShortHash(path)}›";
    }

    /// <summary>
    /// 文件名脱敏。没开「原始文件名」时只留短哈希——
    /// 与「隐藏文件名」功能一致：日志里不能出现真实名字。
    /// </summary>
    public static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        return Has(LogField.FileName) ? name : $"‹name:{ShortHash(name)}›";
    }

    private static string ShortHash(string text)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    public static string LogDirectory
    {
        get
        {
            if (_logDirectory is null)
            {
                _logDirectory = ResolveDirectory();
            }

            return _logDirectory;
        }
    }

    /// <summary>
    /// 依次尝试几个位置，用第一个真能创建的。
    /// 受限环境（只允许某个目录写入）下，%LOCALAPPDATA% 可能被拒，
    /// 这时退到程序目录，再退到系统临时目录。
    /// </summary>
    private static string ResolveDirectory()
    {
        string? custom = System.Environment.GetEnvironmentVariable("CBOX_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            TryCreate(custom);
            return custom;
        }

        var candidates = new[]
        {
            Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "CipherBox", "logs"),
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Path.GetTempPath(), "CipherBox-logs"),
        };

        foreach (var candidate in candidates)
        {
            if (TryCreate(candidate))
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private static bool TryCreate(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Debug(string message) => Write("DEBUG", message);
    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null)
    {
        var text = message;

        if (exception is not null)
        {
            // 堆栈里可能带完整路径，属于敏感字段
            text += Has(LogField.ErrorStack)
                ? Environment.NewLine + DescribeException(exception)
                : $" ({exception.GetType().Name})";
        }

        Write("ERROR", text);
    }

    /// <summary>把异常摊平成多行，包含类型、消息、HResult 与完整堆栈。</summary>
    public static string DescribeException(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        int depth = 0;

        while (current is not null)
        {
            builder.Append($"  [{depth}] {current.GetType().FullName}: {current.Message}")
                   .Append($" (HResult=0x{current.HResult:X8})")
                   .AppendLine();

            if (current.StackTrace is not null)
            {
                foreach (var line in current.StackTrace.Split(Environment.NewLine))
                {
                    builder.Append("      ").AppendLine(line.Trim());
                }
            }

            current = current.InnerException;
            depth++;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>崩溃日志：单独文件，不受开关影响。</summary>
    public static void Crash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            string text =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch
        {
            // 日志不能反过来把程序搞崩
        }
    }

    /// <summary>记录一次运行环境快照，排查环境差异时很有用。</summary>
    public static void Snapshot()
    {
        // 环境快照属于敏感字段（用户名、磁盘、TEMP 都是指纹）
        if (!Has(LogField.Snapshot))
        {
            return;
        }

        try
        {
            Info("===== 环境快照 =====");
            Info($"程序版本      : {CbxVersion.Current}");
            Info($"操作系统      : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            Info($"运行时        : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Info($"进程架构      : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Info($"用户          : {System.Environment.UserDomainName}\\{System.Environment.UserName}");
            Info($"当前目录      : {System.Environment.CurrentDirectory}");
            Info($"日志目录      : {LogDirectory}");
            Info($"系统 TEMP     : {Path.GetTempPath()}");

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady)
                    {
                        Info($"磁盘 {drive.Name} 可用 {drive.AvailableFreeSpace / 1024 / 1024} MB / 共 {drive.TotalSize / 1024 / 1024} MB ({drive.DriveFormat})");
                    }
                }
                catch
                {
                    // 单个磁盘查不到就算了
                }
            }
        }
        catch (Exception ex)
        {
            Warn($"环境快照失败：{ex.Message}");
        }
    }

    /// <summary>探测某个目录能否写入：真的建一个临时文件再删掉，失败时把异常记全。</summary>
    public static void ProbeWritable(string directory, string label)
    {
        // 目录 ACL / 属性属于敏感字段
        if (!Has(LogField.Acl))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                Warn($"{label} 写入探测：目录不存在 {directory}");
                return;
            }

            var info = new DirectoryInfo(directory);
            Debug($"{label} 目录属性：{info.Attributes}，存在=True");
            Debug($"{label} 目录 ACL 摘要：{DescribeDirectoryAcl(directory)}");

            string probe = Path.Combine(directory, $".cbox_probe_{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            Info($"{label} 写入探测：成功（{directory}）");
        }
        catch (Exception ex)
        {
            Warn($"{label} 写入探测：失败（{directory}）" + Environment.NewLine + DescribeException(ex));
        }
    }

    private static string DescribeDirectoryAcl(string directory)
    {
        try
        {
            var security = new DirectoryInfo(directory).GetAccessControl();
            var parts = new List<string>
            {
                $"所有者={security.GetOwner(typeof(System.Security.Principal.NTAccount))?.Value ?? "未知"}",
            };

            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount));
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
            {
                string identity = rule.IdentityReference.Value;
                if (!identity.Contains(System.Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                    !identity.Contains("Everyone", StringComparison.OrdinalIgnoreCase) &&
                    !identity.Contains("Users", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add($"{rule.AccessControlType} {identity} {rule.FileSystemRights}");
            }

            return string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            return $"读取失败（{ex.GetType().Name}）";
        }
    }

    private static void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                // 每个版本一个文件，后面再按天切：cipherbox-v1.2.20-20261115.log
                // 全塞进一个文件的话，测几轮之后找某次操作就得翻半天。
                string path = Path.Combine(
                    LogDirectory,
                    $"cipherbox-v{CbxVersion.Current}-{DateTime.Now:yyyyMMdd}.log");

                var builder = new StringBuilder();

                // 本次运行的第一行永远是版本号，方便一眼确认"测的是哪个构建"
                if (!_sessionHeaderWritten)
                {
                    _sessionHeaderWritten = true;
                    builder.Append(Environment.NewLine)
                           .Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO ] ")
                           .Append($"===== {CbxVersion.Display} 启动 pid={System.Environment.ProcessId} =====")
                           .Append(Environment.NewLine);
                }

                builder.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}")
                       .Append(Environment.NewLine);

                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 写日志失败不能影响主流程
            }
        }
    }
    // ---------------- 设置的持久化 ----------------

    /// <summary>设置文件。只存非敏感字段；敏感位每次启动都是关的。</summary>
    private static string SettingsPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "CipherBox", "log-fields.txt");

    public static void LoadSettings()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
            {
                return;
            }

            if (!int.TryParse(File.ReadAllText(path).Trim(), out int value))
            {
                return;
            }

            // 加载层强制过滤：配置文件被手改成含敏感位也没用
            _fields = (LogField)value & ~LogFields.SensitiveMask;
        }
        catch
        {
            // 读不到就用默认（全关）
        }
    }

    public static void SaveSettings()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 持久化层过滤：敏感位永远写不进去
            int persisted = (int)(_fields & ~LogFields.SensitiveMask);
            File.WriteAllText(path, persisted.ToString());
        }
        catch
        {
            // 存不下就算了
        }
    }
}