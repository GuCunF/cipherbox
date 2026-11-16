using System.Windows;
using System.Windows.Threading;
using CBox.Core;

namespace CBox.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 语言必须在任何界面文字被解析之前加载——XAML 里的 {ui:Tr} 会在窗口构造时取值
        CBox.Core.Localization.Initialize();

        // 未处理异常一律留底，崩溃日志默认开启（不含任何文件内容）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CbxLog.Crash(e.Exception);
        MessageBox.Show(
            UiText.Get("crash.message", CbxLog.LogDirectory) + "\n\n" + e.Exception.Message,
            UiText.Get("app.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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
