using CBox.Core;

// 密码学实现的正确性测试。
// 用公开标准向量验证——自己写的密码学如果只跟自己比，写错了也测不出来。

int passed = 0;
int failed = 0;

void Check(string name, string expected, string actual)
{
    if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  PASS  {name}");
        passed++;
    }
    else
    {
        Console.WriteLine($"  FAIL  {name}");
        Console.WriteLine($"        期望 {expected}");
        Console.WriteLine($"        实际 {actual}");
        failed++;
    }
}

static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

// ============================================================
Console.WriteLine("=== BLAKE2b (RFC 7693 附录 A) ===");
// ============================================================

Check(
    "BLAKE2b-512 空字符串",
    "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce",
    Hex(Blake2b.Hash(Array.Empty<byte>())));

Check(
    "BLAKE2b-512 \"abc\"",
    "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
    Hex(Blake2b.Hash("abc"u8)));

Check(
    "BLAKE2b-512 狐狸句子",
    "a8add4bdddfd93e4877d2746e62817b116364a1fa7bc148d95090bc7333b3673f82401cf7aa2e4cb1ecd90296e3f14cb5413f8ed77be73045b13914cdcd6a918",
    Hex(Blake2b.Hash("The quick brown fox jumps over the lazy dog"u8)));

// ============================================================
Console.WriteLine();
Console.WriteLine("=== BLAKE2b 变长输出 ===");
// ============================================================

byte[] longInput = new byte[200];
for (int i = 0; i < longInput.Length; i++) longInput[i] = (byte)i;

byte[] out32 = Blake2b.Hash(longInput, 32);
byte[] out64 = Blake2b.Hash(longInput, 64);

Check("BLAKE2b-256 长度", "32", out32.Length.ToString());
Check("BLAKE2b-512 长度", "64", out64.Length.ToString());
Check("不同输出长度结果不同", "不同",
    out32.SequenceEqual(out64.AsSpan(0, 32)) ? "相同" : "不同");

var inc = new Blake2b.State(64);
inc.Update(longInput.AsSpan(0, 50));
inc.Update(longInput.AsSpan(50, 100));
inc.Update(longInput.AsSpan(150));
Check("增量 == 一次性", Hex(Blake2b.Hash(longInput)), Hex(inc.FinalizeHash()));

// ============================================================
Console.WriteLine();
Console.WriteLine("=== Argon2id (RFC 9106 第 5.3 节) ===");
// ============================================================
// 官方测试向量：m=32KiB, t=3, p=4, T=32，带 Secret 和 AssociatedData
// 注意取值：Password=01，Salt=02，Secret=03，AD=04（RFC 9106 第 5.3 节）

byte[] password = new byte[32];
Array.Fill(password, (byte)0x01);

byte[] salt = new byte[16];
Array.Fill(salt, (byte)0x02);

byte[] secret = new byte[8];
Array.Fill(secret, (byte)0x03);

byte[] ad = new byte[12];
Array.Fill(ad, (byte)0x04);

const string expectedTag =
    "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659";

// RFC 9106 第 5.3 节给出的 Pre-hashing digest（H0），先单独验它
const string expectedH0 =
    "2889de487eb42ae500c0007ed9252f10" +
    "69eadec40d5765b485de6dc2437a67b8" +
    "546a2f0acc1a0882db8fcf74714b472e" +
    "94df421a5da1112ffa11434370a1e997";

try
{
    byte[] h0 = Argon2Core.DebugComputeH0(
        password, salt,
        iterations: 3,
        memoryKiB: 32,
        outputLength: 32,
        lanes: 4,
        secret: secret,
        associatedData: ad);

    Check("RFC 9106 H0（Pre-hashing digest）", expectedH0, Hex(h0));
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL  H0 抛异常: {ex.GetType().Name}: {ex.Message}");
    failed++;
}

// RFC 9106 给出的「After pass 2」（3 轮跑完）下 Block 0000 的前 4 个字（小端）
const string expectedBlock0 =
    "942363968ce597a4" + "a22448c0bdad5760" + "a5f80662b6fa8748" + "a0f9b9ce392f719f";

// 先单独验 H'^1024(H0 || LE32(0) || LE32(0)) 的前 32 字节
try
{
    byte[] h0Bytes = Convert.FromHexString(expectedH0);
    var seed = new byte[72];
    h0Bytes.CopyTo(seed, 0);
    // 后 8 字节保持 0（LE32(0) 和 LE32(lane=0)）

    byte[] block0Bytes = Argon2Core.DebugVariableLengthHash(seed, 1024);

    // 期望值：RFC 的 4 个 ulong 按小端还原成字节
    const string expectedBytes =
        "43bd7106f1092e6b" + "be218a91275c9ff6" + "e19012a40e81a7de" + "93f8701817f78767";

    Check("H'^1024 初始化块前 32 字节", expectedBytes, Hex(block0Bytes.AsSpan(0, 32)));
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL  H' 诊断抛异常: {ex.GetType().Name}: {ex.Message}");
    failed++;
}

try
{
    ulong[] memory = Argon2Core.DebugFillMemory(
        password, salt,
        iterations: 3,
        memoryKiB: 32,
        outputLength: 32,
        lanes: 4,
        secret: secret,
        associatedData: ad);

    var sb = new System.Text.StringBuilder();

    for (int i = 0; i < 4; i++)
    {
        sb.Append(memory[i].ToString("x16"));
    }

    Check("RFC 9106 填充后 Block 0000 前 4 字", expectedBlock0, sb.ToString());

    Array.Clear(memory);
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL  填充诊断抛异常: {ex.GetType().Name}");
    failed++;
}

try
{
    byte[] tag = Argon2Core.Derive(
        password, salt,
        iterations: 3,
        memoryKiB: 32,
        outputLength: 32,
        lanes: 4,
        secret: secret,
        associatedData: ad);

    Check("RFC 9106 Argon2id 官方向量", expectedTag, Hex(tag));
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL  抛异常: {ex.GetType().Name}");
    Console.WriteLine(ex.ToString());
    failed++;
}

// ============================================================
Console.WriteLine();
Console.WriteLine("=== Argon2id 实用参数（p=1）===");
// ============================================================

// 没有公开的 p=1 向量，这里只验证：同样输入两次结果一致、不同口令结果不同
byte[] pw1 = "test-password"u8.ToArray();
byte[] salt1 = new byte[16];
Random.Shared.NextBytes(salt1);

byte[] a = Argon2Core.Derive(pw1, salt1, iterations: 1, memoryKiB: 1024, outputLength: 32);
byte[] b = Argon2Core.Derive(pw1, salt1, iterations: 1, memoryKiB: 1024, outputLength: 32);

Check("同输入结果可复现", Hex(a), Hex(b));
Check("输出长度正确", "32", a.Length.ToString());

byte[] differentPw = "test-password2"u8.ToArray();
byte[] c = Argon2Core.Derive(differentPw, salt1, iterations: 1, memoryKiB: 1024, outputLength: 32);
Check("不同口令结果不同", "不同", a.SequenceEqual(c) ? "相同" : "不同");

byte[] salt2 = new byte[16];
Random.Shared.NextBytes(salt2);
byte[] d = Argon2Core.Derive(pw1, salt2, iterations: 1, memoryKiB: 1024, outputLength: 32);
Check("不同盐结果不同", "不同", a.SequenceEqual(d) ? "相同" : "不同");

// ============================================================
Console.WriteLine();
Console.WriteLine($"结果：{passed} 通过 / {failed} 失败");
return failed == 0 ? 0 : 1;
