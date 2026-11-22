using CBox.Core;

string work = args[0];
Directory.CreateDirectory(work);

byte[] pw = System.Text.Encoding.UTF8.GetBytes("keypath-pass");
string keyfile = Path.Combine(work, "key.bin");
File.WriteAllBytes(keyfile, System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
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

bool ok = true;

// 1) 单文件
{
    string src = Path.Combine(work, "single.txt");
    string dst = Path.Combine(work, "single.cbx");
    File.WriteAllText(src, "single");
    File.Delete(dst);
    Cbx.EncryptFile(src, dst, pw, profile);
    var h = Cbx.ReadHeader(dst);
    Console.WriteLine($"[单文件] 需要钥匙={h.RequiresKeyFile}");
    ok &= h.RequiresKeyFile;
}

// 2) 文件夹归档
{
    string dir = Path.Combine(work, "folder");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "a.txt"), "archive content");
    string dst = Path.Combine(work, "archive.cbx");
    File.Delete(dst);
    CbxArchive.Create(dir, dst, pw, profile, previewable: true);
    var h = Cbx.ReadHeader(dst);
    Console.WriteLine($"[归档] 需要钥匙={h.RequiresKeyFile}");
    ok &= h.RequiresKeyFile;
}

// 3) 批量加密
{
    string dir = Path.Combine(work, "batchsrc");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "b.txt"), "batch content");
    string outDir = Path.Combine(work, "batchout");
    Directory.CreateDirectory(outDir);
    CbxBatch.Encrypt(dir, outDir, pw, profile);
    string first = Directory.GetFiles(outDir, "*.cbx", SearchOption.AllDirectories).First();
    var h = Cbx.ReadHeader(first);
    Console.WriteLine($"[批量] 需要钥匙={h.RequiresKeyFile}");
    ok &= h.RequiresKeyFile;
}

Console.WriteLine(ok ? "结果：三条路径都带上了钥匙" : "结果：有路径漏了钥匙");
return ok ? 0 : 1;
