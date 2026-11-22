using CBox.Core;

string work = args[0];
Directory.CreateDirectory(work);

byte[] pw = System.Text.Encoding.UTF8.GetBytes("keyfile-pass-123");
string plain = Path.Combine(work, "secret.txt");
string keyfile = Path.Combine(work, "my-key.dat");
string cipher = Path.Combine(work, "secret.cbx");
string outDir = Path.Combine(work, "out");

File.WriteAllText(plain, "这份内容需要口令 + 密钥文件才能打开");
File.WriteAllBytes(keyfile, System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
File.Delete(cipher);

byte[] fp = KeyFile.FingerprintFromFile(keyfile);

// 用 keyfile 加密
Cbx.EncryptFile(plain, cipher, pw, new CbxProfile
{
    Name = "标准",
    AlgorithmId = CbxFormat.DefaultAlgId,
    KdfId = 2,
    Iterations = 3,
    ChunkSize = CbxFormat.DefaultChunkSize,
    KeyFileFingerprint = fp,
});

var header = Cbx.ReadHeader(cipher);
Console.WriteLine($"[加密后] 需要密钥文件={header.RequiresKeyFile} KDF={header.KdfId}");

// 1) 不给 keyfile → 应该报错
try
{
    Cbx.DecryptFile(cipher, Path.Combine(work, "nokey"), pw);
    Console.WriteLine("[不给密钥文件] ❌ 竟然成功了");
}
catch (CbxException ex)
{
    Console.WriteLine($"[不给密钥文件] ✅ 正确拒绝: {ex.Message}");
}

// 2) 给错的 keyfile → 应该失败
try
{
    byte[] wrongFp = KeyFile.Fingerprint(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    Cbx.DecryptFile(cipher, Path.Combine(work, "wrongkey"), pw, null, null, wrongFp);
    Console.WriteLine("[错误密钥文件] ❌ 竟然成功了");
}
catch (CbxException)
{
    Console.WriteLine("[错误密钥文件] ✅ 正确拒绝");
}

// 3) 给对的 keyfile → 成功
try
{
    Cbx.DecryptFile(cipher, outDir, pw, null, null, fp);
    string content = File.ReadAllText(Path.Combine(outDir, "secret.txt")).Trim();
    Console.WriteLine($"[正确密钥文件] ✅ 解密成功: [{content}]");
    Console.WriteLine(content == "这份内容需要口令 + 密钥文件才能打开" ? "结果：通过" : "结果：失败");
}
catch (Exception ex)
{
    Console.WriteLine($"[正确密钥文件] ❌ 失败: {ex.Message}");
}

// 4) 普通文件（不用 keyfile）仍能正常解密
string plain2 = Path.Combine(work, "normal.txt");
string cipher2 = Path.Combine(work, "normal.cbx");
File.WriteAllText(plain2, "普通文件");
File.Delete(cipher2);
Cbx.EncryptFile(plain2, cipher2, pw, CbxProfile.StandardWith(2));
var h2 = Cbx.ReadHeader(cipher2);
Console.WriteLine($"[普通文件] 需要密钥文件={h2.RequiresKeyFile}");
Cbx.DecryptFile(cipher2, Path.Combine(work, "normalout"), pw);
Console.WriteLine("[普通文件解密] ✅ 成功");

return 0;
