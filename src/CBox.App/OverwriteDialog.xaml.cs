using System.IO;
using System.Windows;
using CBox.Core;

namespace CBox.App;

public enum OverwriteChoice
{
    Cancel,
    Replace,
    SaveAsNew,
}

/// <summary>
/// 目标文件已存在时的选择框。
///
/// 不用 MessageBox 的「是/否/取消」——那三个字和"替换/另存/取消"对不上，极其容易点错。
/// 这里用三个写明动作的按钮，并且默认焦点在「另存为新文件」上：
/// 直接回车不会覆盖任何东西。
/// </summary>
public partial class OverwriteDialog : Window
{
    public OverwriteDialog(string path)
    {
        InitializeComponent();

        PathText.Text = path;

        try
        {
            var info = new FileInfo(path);
            InfoText.Text = info.Exists
                ? UiText.Get("overwrite.info", info.Length, info.LastWriteTime)
                : string.Empty;
        }
        catch
        {
            InfoText.Text = string.Empty;
        }
    }

    public OverwriteChoice Choice { get; private set; } = OverwriteChoice.Cancel;

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.Replace;
        DialogResult = true;
    }

    private void SaveAsNew_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.SaveAsNew;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.Cancel;
        DialogResult = false;
    }

    /// <summary>自绘标题栏的关闭按钮。</summary>
    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();
}
