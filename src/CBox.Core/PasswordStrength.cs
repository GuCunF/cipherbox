using System.Text.RegularExpressions;

namespace CBox.Core;

/// <summary>口令强度档位。粗粒度三档，不做字典比对。</summary>
public enum PasswordLevel
{
    Weak = 0,
    Medium = 1,
    Strong = 2,
}

/// <summary>
/// 口令强度**粗**检测。
///
/// 只做可解释的判断，不装神弄鬼：
///   1. 长度、字符种类 → 基础分
///   2. 明显模式（全同一字符、连号、键盘序、年份结尾）→ 降档
///
/// 明确不做：
///   - 不联网、不查泄露库
///   - 不做熵估算（那需要词典，属于"细检测"，后续版本再加强）
///
/// 只在**加密**时提示；解密时不评判——否则等于告诉旁人"这个文件的口令很弱"。
/// </summary>
public static class PasswordStrength
{
    /// <summary>评估口令强度。</summary>
    public static PasswordLevel Evaluate(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordLevel.Weak;
        }

        int length = password.Length;
        int classes = CountClasses(password);

        // 基础档
        PasswordLevel level;

        if (length < 8)
        {
            level = PasswordLevel.Weak;
        }
        else if (length >= 12 && classes >= 3)
        {
            level = PasswordLevel.Strong;
        }
        else if (length >= 10 && classes >= 2)
        {
            level = PasswordLevel.Medium;
        }
        else
        {
            level = PasswordLevel.Weak;
        }

        // 明显模式一律降一档
        if (level > PasswordLevel.Weak && LooksPredictable(password))
        {
            level--;
        }

        return level;
    }

    /// <summary>是否"看起来好猜"：单一字符、纯连号、键盘序、常见词。</summary>
    public static bool LooksPredictable(string password)
    {
        if (password.Length == 0)
        {
            return true;
        }

        // 全同一个字符
        if (password.Distinct().Count() == 1)
        {
            return true;
        }

        string lower = password.ToLowerInvariant();

        // 键盘序 / 常见弱口令
        string[] common =
        {
            "qwerty", "asdf", "zxcv", "123456", "654321", "abcdef", "password", "passwd",
            "admin", "letmein", "iloveyou", "monkey", "dragon", "abc123", "qazwsx",
        };

        if (common.Any(lower.Contains))
        {
            return true;
        }

        // 纯数字 + 递增/递减序列
        if (password.All(char.IsDigit))
        {
            bool ascending = true;
            bool descending = true;

            for (int i = 1; i < password.Length; i++)
            {
                if (password[i] - password[i - 1] != 1)
                {
                    ascending = false;
                }

                if (password[i] - password[i - 1] != -1)
                {
                    descending = false;
                }
            }

            if (ascending || descending)
            {
                return true;
            }
        }

        // 形如 aaaaa1111（重复片段）
        if (Regex.IsMatch(lower, @"^(.)\1{3,}$"))
        {
            return true;
        }

        return false;
    }

    private static int CountClasses(string password)
    {
        int classes = 0;

        if (password.Any(char.IsLower))
        {
            classes++;
        }

        if (password.Any(char.IsUpper))
        {
            classes++;
        }

        if (password.Any(char.IsDigit))
        {
            classes++;
        }

        if (password.Any(c => !char.IsLetterOrDigit(c)))
        {
            classes++;
        }

        return classes;
    }
}
