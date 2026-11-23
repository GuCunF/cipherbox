using System.Security.Cryptography;
using System.Text;

namespace CBox.App;

/// <summary>
/// 口令的 UTF-8 字节包装：<see cref="Dispose"/> 时自动涂零。
///
/// <para>
/// 存在的意义：让「用完就擦」跟着作用域自动发生，
/// 而不是指望每个 return / catch / await 分支都记得手动擦一遍。
/// </para>
///
/// <para>
/// 用法：
/// <code>
/// using var pwd = PasswordBytes.From(password);
/// var report = await Task.Run(() => Cbx.EncryptFile(input, output, pwd.Value, profile));
/// // 方法结束（含抛异常、含 await 中断）时自动涂零
/// </code>
/// </para>
///
/// <para>
/// 注意：这里擦的是**字节数组**。
/// WPF 的 <c>PasswordBox.Password</c> 返回的是不可变 <c>string</c>，擦不掉——
/// 那是平台限制，不是这里能解决的。
/// </para>
/// </summary>
internal sealed class PasswordBytes : IDisposable
{
    private bool _disposed;

    public byte[] Value { get; }

    private PasswordBytes(byte[] value) => Value = value;

    /// <summary>把口令编码成 UTF-8 字节。</summary>
    public static PasswordBytes From(string password) => new(Encoding.UTF8.GetBytes(password));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(Value);
    }
}
