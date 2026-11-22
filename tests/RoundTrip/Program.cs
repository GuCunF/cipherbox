using CBox.Core;

string work = args[0];
Directory.CreateDirectory(work);

byte[] pw = System.Text.Encoding.UTF8.GetBytes("argon2-test-password");
string plain = Path.Combine(work, "hello.txt");
string cipher = Path.Combine(work, "hello.cbx");
string outDir = Path.Combine(work, "out");

File.WriteAllText(plain, "Argon2id 端到端测试内容");
File.Delete(cipher);

// 用 Argon2id 加密
var profile = CbxProfile.Standard;
var kdf = KdfRegistry.Get(2);

Console.WriteLine($"KDF: {kdf.DisplayName}, 默认迭代 {kdf.DefaultIterations}");

var sw = System.Diagnostics.Stopwatch.StartNew();
Cbx.EncryptFile(plain, cipher, pw, new CbxProfile
{
    Name = profile.Name,
    AlgorithmId = profile.AlgorithmId,
    KdfId = 2,                          // Argon2id
    Iterations = 3,
    ChunkSize = profile.ChunkSize,
    SkipTypeCheck = profile.SkipTypeCheck,
    EncryptName = profile.EncryptName,
    HideCategory = profile.HideCategory,
    WriteCategory = profile.WriteCategory,
});
sw.Stop();
Console.WriteLine($"加密耗时 {sw.ElapsedMilliseconds} ms");

var header = Cbx.ReadHeader(cipher);
Console.WriteLine($"文件头 KDF={header.KdfId} 迭代={header.KdfIterations}");

// 解密
sw.Restart();
Cbx.DecryptFile(cipher, outDir, pw);
sw.Stop();
Console.WriteLine($"解密耗时 {sw.ElapsedMilliseconds} ms");

string content = File.ReadAllText(Path.Combine(outDir, "hello.txt")).Trim();
Console.WriteLine($"解密内容: [{content}]");
Console.WriteLine(content == "Argon2id 端到端测试内容" ? "结果：通过" : "结果：失败");
return content == "Argon2id 端到端测试内容" ? 0 : 1;
