using System.Windows;
using CBox.Core;

namespace CBox.App;

public enum ConflictChoice
{
    Cancel,
    Overwrite,
    Skip,
}

/// <summary>
/// 解压时目标文件已存在的选择框。
///
/// 加解密程序里最忌讳「是 / 否」——用户会问"是覆盖还是是跳过"。
/// 所以三个按钮都写清动作，并且：
///   - 默认焦点在「跳过这个文件」，直接回车不会覆盖任何东西
///   - 「取消解压」用 IsCancel，按 Esc 等于中止整次解压
///   - 勾选框负责"对之后所有冲突都用这个选择"
/// </summary>
public partial class ConflictDialog : Window
{
    public ConflictDialog(string path)
    {
        InitializeComponent();
        PathText.Text = path;
    }

    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;

    /// <summary>是否对之后所有冲突都使用本次选择。</summary>
    public bool ApplyToAll => ApplyAllCheck.IsChecked == true;

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.Overwrite;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.Skip;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.Cancel;
        DialogResult = false;
    }
}
