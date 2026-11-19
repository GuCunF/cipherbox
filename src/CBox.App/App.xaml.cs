using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using CBox.Core;

namespace CBox.App;

public partial class App : Application
{
    /// <summary>
    /// 同一时间只允许一个实例。第二个实例不自己开窗口，
    /// 而是把「要打开的文件」通过命名管道交给第一个实例，然后安静退出。
    ///
    /// 这样双击十个 .cbx 也不会开出十个窗口。
    /// </summary>
    private const string InstanceMutexName = @"Local\CipherBox.SingleInstance";

    private const string PipeName = "CipherBox.OpenPath";

    /// <summary>启动参数里带来的文件路径（可能为空）。</summary>
    public static string? StartupPath { get; private set; }

    /// <summary>
    /// 主窗口还没准备好时收到的路径先存这里，等窗口加载完再取。
    /// 典型场景：程序正在启动，用户又双击了一个文件。
    /// </summary>
    private static readonly Queue<string> PendingPaths = new();

    private Mutex? _instanceMutex;
    private bool _primaryInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 第一个非选项参数当成「要打开的文件」。
        // 拖到 exe 图标、右键菜单、双击关联文件都会走到这里。
        StartupPath = e.Args.FirstOrDefault(a => !a.StartsWith('-') && !string.IsNullOrWhiteSpace(a));

        // ---- 单实例判定 ----
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _primaryInstance);

        // 诊断：把单实例判定结果记下来，方便排查「为什么开了两个窗口」
        CbxLog.Info($"单实例判定：{( _primaryInstance ? "主实例" : "副实例")}，参数 {StartupPath ?? "(无)"}");

        if (!_primaryInstance)
        {
            // 已经有一个在跑了：把路径递过去，然后立刻退出。
            // 这里必须用 Environment.Exit 而不是 Shutdown()——Shutdown 是"请求关闭"，
            // WPF 可能还会继续把窗口创建流程走完，留下一个闪一下的空窗口。
            if (!string.IsNullOrEmpty(StartupPath))
            {
                ForwardToRunningInstance(StartupPath!);
            }

            Environment.Exit(0);
        }

        base.OnStartup(e);

        // 语言必须在任何界面文字被解析之前加载——XAML 里的 {ui:Tr} 会在窗口构造时取值
        CBox.Core.Localization.Initialize();

        // 未处理异常一律留底，崩溃日志默认开启（不含任何文件内容）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 开着管道等后续实例递文件进来
        StartPipeServer();

        // 主窗口手动创建（App.xaml 里已去掉 StartupUri）
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_primaryInstance)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>主窗口加载完后调用：把启动参数里的路径交给它。</summary>
    public static string? TakeStartupPath()
    {
        string? path = StartupPath;
        StartupPath = null;
        return path;
    }

    /// <summary>取走排队中的路径（可能有多条）。</summary>
    public static string[] TakePendingPaths()
    {
        lock (PendingPaths)
        {
            if (PendingPaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            string[] all = PendingPaths.ToArray();
            PendingPaths.Clear();
            return all;
        }
    }

    // ---------------- 第二个实例：投递路径 ----------------

    private static void ForwardToRunningInstance(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);

            using var writer = new StreamWriter(client);
            writer.Write(path);
            writer.Flush();
        }
        catch
        {
            // 管道连不上不致命：最坏情况是这次双击没反应，程序本身仍可正常使用
        }
    }

    // ---------------- 第一个实例：接收路径 ----------------

    private void StartPipeServer()
    {
        var thread = new Thread(PipeLoop)
        {
            IsBackground = true,
            Name = "CipherBox.OpenPathPipe",
        };

        thread.Start();
    }

    private void PipeLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server);
                string path = reader.ReadToEnd().Trim();

                if (path.Length > 0)
                {
                    Dispatcher.Invoke(() => Deliver(path));
                }
            }
            catch
            {
                // 管道被占用/关掉都按「稍后重试」处理，不让后台线程死掉
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>把路径交给主窗口；窗口还没准备好就先排队。</summary>
    private static void Deliver(string path)
    {
        if (Current?.MainWindow is MainWindow window && window.IsLoaded)
        {
            window.OpenExternalPath(path);
            return;
        }

        lock (PendingPaths)
        {
            PendingPaths.Enqueue(path);
        }
    }

    // ---------------- 异常留底 ----------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CbxLog.Crash(e.Exception);
        AppDialog.Error(UiText.Get("app.title"), UiText.Get("crash.message", CbxLog.LogDirectory) + Environment.NewLine + Environment.NewLine + e.Exception.Message);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CbxLog.Crash(exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CbxLog.Crash(e.Exception);
        e.SetObserved();
    }
}
