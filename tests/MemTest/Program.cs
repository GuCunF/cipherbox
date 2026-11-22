using System.Diagnostics;
using CBox.Core;

string work = args[0];
Directory.CreateDirectory(work);
byte[] pw = System.Text.Encoding.UTF8.GetBytes("mem-test-pass-123");

foreach (int mem in new[] { 32, 64, 128 })
{
    string src = Path.Combine(work, $"m{mem}.txt");
    string dst = Path.Combine(work, $"m{mem}.cbx");
    string outDir = Path.Combine(work, $"out{mem}");
    File.WriteAllText(src, $"memory {mem} MiB test");
    File.Delete(dst);

    var profile = new CbxProfile
    {
        Name = "测试",
        AlgorithmId = CbxFormat.DefaultAlgId,
        KdfId = 2,
        Iterations = 3,
        Argon2MemoryMiB = mem,
        ChunkSize = CbxFormat.DefaultChunkSize,
    };

    var sw = Stopwatch.StartNew();
    Cbx.EncryptFile(src, dst, pw, profile);
    sw.Stop();

    var h = Cbx.ReadHeader(dst);
    var (rounds, decodedMem) = Argon2idKdf.Decode(h.KdfIterations);

    var sw2 = Stopwatch.StartNew();
    Cbx.DecryptFile(dst, outDir, pw);
    sw2.Stop();

    string content = File.ReadAllText(Path.Combine(outDir, $"m{mem}.txt")).Trim();
    bool ok = content == $"memory {mem} MiB test" && decodedMem == mem && rounds == 3;
    Console.WriteLine($"[{mem} MiB] 存值={h.KdfIterations} 解出内存={decodedMem} 轮数={rounds} 加密={sw.ElapsedMilliseconds}ms 解密={sw2.ElapsedMilliseconds}ms → {(ok ? "OK" : "FAIL")}");
}

Console.WriteLine("结果：通过");
return 0;
