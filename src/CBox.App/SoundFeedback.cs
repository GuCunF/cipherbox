using System.IO;
using System.Media;

namespace CBox.App;

public enum WaveType
{
    /// <summary>正弦：最柔和的提示音。</summary>
    Sine,

    /// <summary>三角：比正弦略亮，但不刺耳。</summary>
    Triangle,

    /// <summary>方波：最锐利，只用在"明显"档。</summary>
    Square,
}

/// <summary>提示音档位。</summary>
public enum SoundPreset
{
    /// <summary>安静：办公室、深夜。</summary>
    Quiet = 0,

    /// <summary>标准：默认。</summary>
    Standard = 1,

    /// <summary>明显：嘈杂环境、听力较弱。</summary>
    Strong = 2,

    /// <summary>静音：完全不发声，只在界面上闪一下。</summary>
    Silent = 3,
}

/// <summary>
/// 操作完成的提示音。
///
/// 不用系统音效（SystemSounds），因为：
///   1. 系统音效受用户声音方案影响，很多人直接关掉了；
///   2. "错误"音已经是系统里最重的一档，没法再做得更醒目。
/// 所以这里在内存里现场合成 PCM，音高、时长、音量、波形全部可控，也不涉及任何音频版权。
///
/// 三条规则（用户 2026-11-15 定）：
///   1. 成功音只在操作超过 1 秒时播——短操作几乎瞬间完成，响了反而吵
///   2. 系统关掉了提示音（HKCU\Control Panel\Sound\Beep = no）时，这边也静默
///   3. 静音档完全不发声，改由界面闪烁提示
/// </summary>
public static class SoundFeedback
{
    private const int SampleRate = 44100;

    /// <summary>操作短于这个时长就不播成功音。</summary>
    private static readonly TimeSpan SuccessMinDuration = TimeSpan.FromSeconds(1);

    private static readonly SoundPlayer?[] SuccessPlayers = new SoundPlayer?[4];
    private static readonly SoundPlayer?[] FailurePlayers = new SoundPlayer?[4];

    /// <summary>总开关（界面上可关）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>档位，会持久化。</summary>
    public static SoundPreset Preset { get; private set; } = SoundPreset.Standard;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherBox", "sound.txt");

    public static void Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int value))
            {
                Preset = (SoundPreset)Math.Clamp(value, 0, 3);
            }
        }
        catch
        {
            // 读不到就用标准档
        }
    }

    public static void SetPreset(SoundPreset preset)
    {
        Preset = preset;

        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ((int)preset).ToString());
        }
        catch
        {
            // 存不下也无所谓
        }
    }

    /// <summary>
    /// 播成功音。返回 false 表示"没发出声音"，调用方可以改用界面提示。
    /// 操作太快（短于 1 秒）、静音档、系统静音、总开关关闭，都会返回 false。
    /// </summary>
    public static bool PlaySuccess(TimeSpan? elapsed = null)
    {
        // 传了时长就按"短操作不响"的规则；不传（如保存成功）则照常播
        if (elapsed.HasValue && elapsed.Value < SuccessMinDuration)
        {
            return false;
        }

        return Play(SuccessPlayers, BuildSuccessWav);
    }

    /// <summary>播失败音。失败一律要出声（除非静音档 / 系统静音 / 总开关关闭）。</summary>
    public static bool PlayFailure()
    {
        return Play(FailurePlayers, BuildFailureWav);
    }

    private static bool Play(SoundPlayer?[] slots, Func<byte[]> factory)
    {
        if (!Enabled || Preset == SoundPreset.Silent || SystemSoundsMuted())
        {
            return false;
        }

        try
        {
            int index = (int)Preset;

            slots[index] ??= CreatePlayer(factory());

            slots[index]!.Play();
            return true;
        }
        catch
        {
            // 没有声卡也不该影响主流程
            return false;
        }
    }

    /// <summary>
    /// Windows 是否关掉了提示音。
    /// 读 HKCU\Control Panel\Sound 的 Beep 值：no 表示用户把系统声音关了。
    /// </summary>
    private static bool SystemSoundsMuted()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Sound");
            if (key?.GetValue("Beep") is string beep)
            {
                return beep.Equals("no", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 读不到就当没静音
        }

        return false;
    }

    private static SoundPlayer CreatePlayer(byte[] wav)
    {
        // 流必须一直存活，所以不 Dispose；预加载避免第一次播放有延迟
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    private static byte[] BuildSuccessWav()
    {
        return Preset switch
        {
            SoundPreset.Quiet => BuildWav(new[] { 660.0, 880.0 }, 90, 0, 0.20, WaveType.Sine),
            SoundPreset.Strong => BuildWav(new[] { 784.0, 1046.0 }, 110, 0, 0.40, WaveType.Sine),
            _ => BuildWav(new[] { 660.0, 880.0 }, 90, 0, 0.30, WaveType.Sine),
        };
    }

    private static byte[] BuildFailureWav()
    {
        return Preset switch
        {
            SoundPreset.Quiet => BuildWav(new[] { 660.0, 550.0 }, 60, 40, 0.25, WaveType.Triangle),
            SoundPreset.Strong => BuildWav(new[] { 660.0, 523.0, 392.0, 330.0 }, 80, 40, 0.45, WaveType.Square),
            _ => BuildWav(new[] { 880.0, 740.0, 620.0 }, 70, 45, 0.35, WaveType.Triangle),
        };
    }

    /// <summary>把若干音符合成为 16 位单声道 PCM，并加上标准 WAV 头。</summary>
    private static byte[] BuildWav(double[] frequencies, int toneMs, int gapMs, double volume, WaveType wave)
    {
        int toneSamples = Math.Max(1, SampleRate * toneMs / 1000);
        int gapSamples = Math.Max(0, SampleRate * gapMs / 1000);
        int fadeSamples = Math.Max(1, Math.Min(toneSamples / 8, SampleRate / 200));   // 约 5ms

        using var pcm = new MemoryStream();

        foreach (double frequency in frequencies)
        {
            for (int i = 0; i < toneSamples; i++)
            {
                double time = (double)i / SampleRate;
                double phase = 2 * Math.PI * frequency * time;

                double sampleValue = wave switch
                {
                    WaveType.Square => Math.Sign(Math.Sin(phase)),
                    WaveType.Triangle => 2.0 / Math.PI * Math.Asin(Math.Sin(phase)),
                    _ => Math.Sin(phase),
                };

                // 淡入淡出，避免啪啪的爆音
                double envelope = 1.0;
                if (i < fadeSamples)
                {
                    envelope = (double)i / fadeSamples;
                }
                else if (i > toneSamples - fadeSamples)
                {
                    envelope = (double)(toneSamples - i) / fadeSamples;
                }

                short sample = (short)(sampleValue * envelope * volume * short.MaxValue);
                pcm.Write(BitConverter.GetBytes(sample), 0, 2);
            }

            for (int i = 0; i < gapSamples; i++)
            {
                pcm.WriteByte(0);
                pcm.WriteByte(0);
            }
        }

        byte[] data = pcm.ToArray();
        using var wav = new MemoryStream(44 + data.Length);
        using var writer = new BinaryWriter(wav);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);              // fmt 块大小
        writer.Write((short)1);        // PCM
        writer.Write((short)1);        // 单声道
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);  // 字节率 = 采样率 × 声道 × 位深/8
        writer.Write((short)2);        // 块对齐
        writer.Write((short)16);       // 位深
        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);

        writer.Flush();
        return wav.ToArray();
    }
}
