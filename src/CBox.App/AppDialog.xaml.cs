using System.Windows;
using System.Windows.Media;
using CBox.Core;

namespace CBox.App;

/// <summary>
/// 统一的对话框，用来替代系统的 <see cref="MessageBox"/>。
///
/// 为什么不用 MessageBox：它是系统原生窗口，样式完全由 Windows 决定，
/// 改不了字体、颜色、按钮。所以自己画一个，跟主窗口保持一致。
///
/// 用法（静态方法，模仿 MessageBox 的调用方式）：
///     AppDialog.Info("标题", "正文");
///     AppDialog.Warn("标题", "正文");
///     AppDialog.Error("标题", "正文");
///     if (AppDialog.Confirm("标题", "正文")) { ... }
/// </summary>
public partial class AppDialog : Window
{
    private enum Kind
    {
        Info,
        Warn,
        Error,
        Question,
    }

    private Kind _kind;
    private bool _result;

    private AppDialog()
    {
        InitializeComponent();
    }

    // ---------------- 静态入口 ----------------

    public static void Info(string heading, string body, Window? owner = null)
        => Show(Kind.Info, heading, body, null, null, owner);

    public static void Warn(string heading, string body, Window? owner = null)
        => Show(Kind.Warn, heading, body, null, null, owner);

    public static void Error(string heading, string body, Window? owner = null)
        => Show(Kind.Error, heading, body, null, null, owner);

    /// <summary>两个按钮的确认框，返回 true 表示用户点了主按钮。</summary>
    public static bool Confirm(
        string heading,
        string body,
        string? primary = null,
        string? secondary = null,
        Window? owner = null)
        => Show(Kind.Question, heading, body, primary, secondary, owner);

    private static bool Show(
        Kind kind,
        string heading,
        string body,
        string? primary,
        string? secondary,
        Window? owner)
    {
        var dialog = new AppDialog
        {
            _kind = kind,
        };

        dialog.HeadingText.Text = heading;
        dialog.BodyText.Text = body;

        dialog.PrimaryButton.Content = primary ?? UiText.Get("common.ok");

        bool twoButtons = secondary is not null;

        dialog.SecondaryButton.Content = secondary ?? string.Empty;
        dialog.SecondaryButton.Visibility = twoButtons ? Visibility.Visible : Visibility.Collapsed;

        if (!twoButtons)
        {
            dialog.PrimaryButton.Margin = new Thickness(0);
        }

        dialog.ApplyKind();

        // 所有者：优先传入的窗口，其次主窗口，都没有就居中屏幕
        Window? actualOwner = owner ?? Application.Current?.MainWindow;

        if (actualOwner is not null && !ReferenceEquals(actualOwner, dialog) && actualOwner.IsVisible)
        {
            dialog.Owner = actualOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();

        return dialog._result;
    }

    private void ApplyKind()
    {
        (string glyph, string color, string title) = _kind switch
        {
            Kind.Warn => ("!", "#B7791F", UiText.Get("dialog.warn.title")),
            Kind.Error => ("\u2715", "#C0392B", UiText.Get("dialog.error.title")),
            Kind.Question => ("?", "#1F5FA8", UiText.Get("dialog.confirm.title")),
            _ => ("i", "#1F5FA8", UiText.Get("dialog.info.title")),
        };

        IconGlyph.Text = glyph;
        IconBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

        // 标题栏文字：用窗口标题承载，保持在任务栏/悬停提示里也有意义
        Title = title;

        // 错误与警告时，正文按错误色显示更醒目
        if (_kind == Kind.Error)
        {
            BodyText.Foreground = (Brush)FindResource("BrushText");
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }
}
