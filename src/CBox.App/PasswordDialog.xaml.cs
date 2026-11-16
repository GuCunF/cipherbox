using System.Windows;

namespace CBox.App;

/// <summary>进入编辑前再要一次口令。取消返回 false。</summary>
public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string Password => PasswordInput.Password;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
