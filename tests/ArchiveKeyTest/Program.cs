using CBox.Core;

string work = args[0];
Directory.CreateDirectory(work);
byte[] pw = System.Text.Encoding.UTF8.GetBytes("archive-key-pass");

string keyfile = Path.Combine(work, "key.bin");
File.WriteAllBytes(keyfile, System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
byte[] fp = KeyFile.FingerprintFromFile(keyfile);

var profile = new CbxProfile
{
    Name = "标准",
    AlgorithmId = CbxFormat.DefaultAlgId,
    KdfId = 2,
    Iterations = 3,
    ChunkSize = CbxFormat.DefaultChunkSize,
    KeyFileFingerprint = fp,
};

string srcDir = Path.Combine(work, "src");
Directory.CreateDirectory(srcDir);
for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(srcDir, $"f{i}.txt"), $"content {i}");

string cbx = Path.Combine(work, "arc.cbx");
File.Delete(cbx);
CbxArchive.Create(srcDir, cbx, pw, profile, previewable: true);

var h = Cbx.ReadHeader(cbx);
Console.WriteLine($"[归档] 需要钥匙={h.RequiresKeyFile}");

// 1) 不带钥匙解压 → 必须失败
string out1 = Path.Combine(work, "out-nokey");
if (Directory.Exists(out1)) Directory.Delete(out1, true);
try
{
    CbxArchive.ExtractAll(cbx, out1, pw);
    Console.WriteLine("[不带钥匙解压] ❌ 竟然成功了");
}
catch (CbxException ex)
{
    Console.WriteLine($"[不带钥匙解压] ✅ 正确拒绝: {ex.Message.Split((char)10)[0]}");
}

// 2) 带正确钥匙 → 成功
string out2 = Path.Combine(work, "out-ok");
if (Directory.Exists(out2)) Directory.Delete(out2, true);
try
{
    int n = CbxArchive.ExtractAll(cbx, out2, pw, keyFileFingerprint: fp);
    Console.WriteLine($"[带钥匙解压] ✅ 成功，{n} 个文件");
    foreach (var f in Directory.GetFiles(out2)) Console.WriteLine($"    {Path.GetFileName(f)}");
}
catch (Exception ex)
{
    Console.WriteLine($"[带钥匙解压] ❌ 失败: {ex.Message}");
}

// 3) ReadManifest 带钥匙
try
{
    var entries = CbxArchive.ReadManifest(cbx, pw, fp);
    Console.WriteLine($"[读清单] ✅ {entries.Count} 个条目");
}
catch (Exception ex)
{
    Console.WriteLine($"[读清单] ❌ 失败: {ex.Message}");
}

return 0;
