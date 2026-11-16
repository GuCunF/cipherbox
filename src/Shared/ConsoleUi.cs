using System.Text;

namespace CBox.Core;

/// <summary>
/// 两个客户端共用的控制台交互（口令输入、确认、错误展示）。
/// 放在 Shared 目录，由两个 exe 项目以链接方式引用，避免复制两份。
/// </summary>
public static class ConsoleUi
{
    public static void Initialize()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些重定向环境不允许改编码，忽略
        }
    }

    public static void Banner(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine();
    }

    public static void Info(string message) => Console.WriteLine(message);
    public static void Warn(string message) => Console.WriteLine(UiText.Get("cli.warn", message));

    public static void Error(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(UiText.Get("cli.error", message));
        Console.ForegroundColor = previous;
    }

    /// <summary>读取一行输入；输入被重定向时走普通读取，交互时走隐藏口令模式。</summary>
    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            string? line = Console.ReadLine();
            Console.WriteLine();
            return line ?? string.Empty;
        }

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return builder.ToString();
    }

    /// <summary>加密时要求输入两遍，防止口令打错导致文件永久解不开。</summary>
    public static string ReadNewPassword()
    {
        while (true)
        {
            string first = ReadPassword(UiText.Get("cli.password.prompt"));
            if (first.Length == 0)
            {
                Error(UiText.Get("error.password.empty"));
                continue;
            }

            string second = ReadPassword(UiText.Get("cli.password.again"));
            if (first != second)
            {
                Error(UiText.Get("cli.password.mismatch"));
                continue;
            }

            return first;
        }
    }

    /// <summary>统一的顶层异常处理，保证只打印可读信息。</summary>
    public static int Run(string title, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (CbxException ex)
        {
            Error(ex.Message);
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Error(UiText.Get("cli.file.notfound", ex.FileName));
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            Error(UiText.Get("cli.access.denied"));
            return 2;
        }
        catch (IOException ex)
        {
            Error(UiText.Get("cli.io.failed", ex.Message));
            return 2;
        }
        catch (Exception ex)
        {
            Error(UiText.Get("cli.unexpected", ex.GetType().Name, ex.Message));
            return 1;
        }
    }
}
