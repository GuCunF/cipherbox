using CBox.Core;

// 回归测试：同一个进程里先加密再解密。
// 跨进程的冒烟测不出密钥缓存被涂零的问题（那正是批量解密全失败的原因）。
string root = args[0];
string src = Path.Combine(root, "src");
string enc = Path.Combine(root, "enc");
string dec = Path.Combine(root, "dec");
string singleDir = Path.Combine(root, "single-dec");

bool ok = true;

Directory.CreateDirectory(src);
File.WriteAllText(Path.Combine(src, "a.txt"), "file A content");
File.WriteAllText(Path.Combine(src, "b.txt"), "file B content");
byte[] pw = System.Text.Encoding.UTF8.GetBytes("testpass123");

DerivedKeyCache.Clear();

// 1) 单文件：同进程加密后立即解密
try
{
    string singleOut = Path.Combine(root, "single.cbx");
    File.Delete(singleOut);
    Cbx.EncryptFile(Path.Combine(src, "a.txt"), singleOut, pw, CbxProfile.Standard);
    Cbx.DecryptFile(singleOut, singleDir, pw);
    Console.WriteLine("[单文件往返] 成功");
}
catch (Exception ex)
{
    Console.WriteLine($"[单文件往返] 失败: {ex.Message}");
    ok = false;
}

// 2) 批量：加密 → 校验 → 解密
try
{
    var rep = CbxBatch.Encrypt(src, enc, pw, CbxProfile.Standard);
    Console.WriteLine($"[批量加密] 成功={rep.Succeeded} 失败={rep.Failed}");

    var v = CbxBatch.Verify(enc, pw);
    Console.WriteLine($"[批量校验] Ok={v.Ok}");

    if (!v.Ok)
    {
        Console.WriteLine($"  原因: {v.Message}");
        ok = false;
    }
    else
    {
        var drep = CbxBatch.Decrypt(enc, dec, pw);
        Console.WriteLine($"[批量解密] 成功={drep.Succeeded} 失败={drep.Failed}");

        if (drep.Failed > 0 || drep.Succeeded < 2)
        {
            ok = false;
        }

        // 内容必须对得上
        string a = File.ReadAllText(Path.Combine(dec, "a.txt")).Trim();
        string b = File.ReadAllText(Path.Combine(dec, "b.txt")).Trim();

        if (a != "file A content" || b != "file B content")
        {
            Console.WriteLine($"[内容校验] 不符: a={a} b={b}");
            ok = false;
        }
        else
        {
            Console.WriteLine("[内容校验] 一致");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[批量往返] 失败: {ex.Message}");
    ok = false;
}

Console.WriteLine(ok ? "结果：通过" : "结果：失败");
return ok ? 0 : 1;
