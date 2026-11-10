using NAudio.Wave;

namespace Theater3D.Media.Tests;

[TestClass]
public sealed class VideoWriterTests
{
    private string _folder = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "3DTheater.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // 上半分が赤、下半分が青の BGRA の 1 枚
    private static byte[] RedOverBlue(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                bool top = y < height / 2;
                pixels[i] = (byte)(top ? 0 : 255);
                pixels[i + 2] = (byte)(top ? 255 : 0);
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    [TestMethod]
    public void Mp4WithVideoAndAudioCanBeWrittenAndReadBack()
    {
        const int width = 256;
        const int height = 144;
        const int frames = 15;
        string path = Path.Combine(_folder, "テスト.mp4");
        byte[] picture = RedOverBlue(width, height);
        using (VideoWriter writer = VideoWriter.Create(path, new VideoSettings(width, height, 30, 2_000_000, AudioExportSource.SampleRate, 2)))
        {
            var audio = new short[AudioExportSource.SampleRate / 30 * 2]; // 1 フレーム分（無音）
            for (int i = 0; i < frames; i++)
            {
                writer.WriteAudio(audio);
                writer.WriteVideo(picture, width * 4, i);
            }
            writer.Finish();
        }

        byte[] first = VideoWriter.ReadFirstFrame(path, out VideoProbe probe);
        Assert.AreEqual(width, probe.Width);
        Assert.AreEqual(height, probe.Height);
        Assert.AreEqual(30, probe.FrameRate, 0.01);
        Assert.AreEqual(frames / 30.0, probe.DurationSeconds, 0.05, "長さ = フレームの数 ÷ フレームの速さ");
        Assert.IsTrue(probe.HasAudio);
        Assert.AreEqual(AudioExportSource.SampleRate, probe.AudioSampleRate);
        Assert.AreEqual(2, probe.AudioChannels);

        // 上下が逆さまになっていないこと。色の決まり（BT.709）が書く側と読む側で合っていること
        // （合っていないと、赤の緑が 25 くらい、青の緑が 15 くらいずれる。H.264 そのもののずれは数段）
        int top = ((10 * width) + 10) * 4;
        int bottom = (((height - 10) * width) + 10) * 4;
        AssertColor(first, top, 255, 0, 0, "上は赤");
        AssertColor(first, bottom, 0, 0, 255, "下は青");
    }

    private static void AssertColor(byte[] bgra, int index, int r, int g, int b, string message)
    {
        int dr = Math.Abs(bgra[index + 2] - r), dg = Math.Abs(bgra[index + 1] - g), db = Math.Abs(bgra[index] - b);
        Assert.IsTrue(dr <= 8 && dg <= 8 && db <= 8, $"{message}（R={bgra[index + 2]}, G={bgra[index + 1]}, B={bgra[index]}）");
    }

    [TestMethod]
    public void VideoWithoutAudioAndBadSettings()
    {
        string path = Path.Combine(_folder, "音なし.mp4");
        File.WriteAllText(path, "前の中身"); // 同じ名前のファイルがあっても、置き換えて書ける
        using (VideoWriter writer = VideoWriter.Create(path, new VideoSettings(160, 96, 24, 1_000_000)))
        {
            byte[] picture = RedOverBlue(160, 96);
            for (int i = 0; i < 6; i++)
            {
                writer.WriteVideo(picture, 160 * 4, i);
            }
            Assert.ThrowsExactly<VideoException>(() => writer.WriteAudio(new short[100]), "音声なしで作った書き手には音声を書けない");
            writer.Finish();
        }
        VideoProbe probe = VideoWriter.Probe(path);
        Assert.IsFalse(probe.HasAudio);
        Assert.AreEqual(24, probe.FrameRate, 0.01);

        var odd = Assert.ThrowsExactly<VideoException>(() => VideoWriter.Create(Path.Combine(_folder, "奇数.mp4"), new VideoSettings(161, 96, 30, 1_000_000)));
        StringAssert.Contains(odd.Message, "偶数");
        Assert.ThrowsExactly<VideoException>(() => VideoWriter.Create(Path.Combine(_folder, "音.mp4"), new VideoSettings(160, 96, 30, 1_000_000, 22050)));
    }

    [TestMethod]
    public void AudioSourceAlignsResamplesAndAppliesVolume()
    {
        // 44.1 kHz・1 チャンネルの 1 秒（ずっと 0.5）。聞こえないので、鳴らしてもよい作りだが、ここでは鳴らさない
        string wave = Path.Combine(_folder, "一定.wav");
        using (var writer = new WaveFileWriter(wave, WaveFormat.CreateIeeeFloatWaveFormat(44_100, 1)))
        {
            float[] samples = [.. Enumerable.Repeat(0.5f, 44_100)];
            writer.WriteSamples(samples, 0, samples.Length);
        }

        var buffer = new short[AudioExportSource.SampleRate * 2]; // 1 秒・2 チャンネル
        using (var source = new AudioExportSource(wave, -0.5, 1f))
        {
            source.Read(buffer);
        }
        int half = AudioExportSource.SampleRate; // 0.5 秒 × 2 チャンネル
        Assert.IsTrue(buffer[..(half - 2)].ToArray().All(s => s == 0), "前の 0.5 秒は無音（動画の 0 秒が音声の -0.5 秒）");
        Assert.AreEqual(16384, buffer[half + 4000], 300, "その後は音声（0.5 → 16 ビットで約 16384）");
        Assert.AreEqual(buffer[half + 4000], buffer[half + 4001], "1 チャンネルは左右に同じ値");

        using (var quiet = new AudioExportSource(wave, 0.25, 0.5f))
        {
            quiet.Read(buffer);
        }
        Assert.AreEqual(8192, buffer[1000], 300, "音量 0.5 で半分");
        Assert.AreEqual(0, buffer[^2], "音声が終わった後（0.75 秒より後）は無音");

        using var after = new AudioExportSource(wave, 5, 1f);
        after.Read(buffer);
        Assert.IsTrue(buffer.All(s => s == 0), "音声より後から書き出すときは無音");
    }
}
