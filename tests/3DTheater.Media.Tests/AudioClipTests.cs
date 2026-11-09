[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Theater3D.Media.Tests;

/// <summary>音声ファイルの情報と波形（テストの中で WAV を作って確かめる。実際に鳴らすテストは、音の出る装置に頼るので入れない）。</summary>
[TestClass]
public sealed class AudioClipTests
{
    public TestContext TestContext { get; set; } = null!;

    // 毎秒の頭 0.1 秒だけ大きい（0.5）、それ以外は小さい（0.05）音
    private string WriteWav(int seconds, int sampleRate, short channels)
    {
        string path = Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        int count = seconds * sampleRate;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + (count * 2 * channels));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * 2 * channels);
        w.Write((short)(2 * channels));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(count * 2 * channels);
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)sampleRate;
            double amplitude = t - Math.Floor(t) < 0.1 ? 0.5 : 0.05;
            short v = (short)(amplitude * Math.Sin(2 * Math.PI * 440 * t) * short.MaxValue);
            for (int c = 0; c < channels; c++)
            {
                w.Write(v);
            }
        }
        return path;
    }

    [TestMethod]
    public void WavInformationIsRead()
    {
        string path = WriteWav(3, 44100, 2);

        AudioClip clip = AudioClip.Open(path);

        Assert.AreEqual(3.0, clip.Duration.TotalSeconds, 1e-3);
        Assert.AreEqual(44100, clip.SampleRate);
        Assert.AreEqual(2, clip.Channels);
        Assert.IsEmpty(clip.Peaks, "波形は求めるまで空");
    }

    [TestMethod]
    public void PeaksFollowTheLoudness()
    {
        string path = WriteWav(2, 48000, 1);

        float[] peaks = AudioClip.Open(path).ComputePeaks();

        Assert.HasCount(2 * AudioClip.PeaksPerSecond, peaks, "1/100 秒ごと");
        Assert.AreEqual(0.5f, peaks[5], 0.02f, "頭の 0.1 秒は大きい");
        Assert.AreEqual(0.05f, peaks[50], 0.01f, "それ以外は小さい");
        Assert.AreEqual(0.5f, peaks[105], 0.02f, "次の秒の頭も大きい");
    }

    [TestMethod]
    public void AudioFilesAreRecognizedByExtension()
    {
        Assert.IsTrue(AudioClip.IsAudioFile(@"C:\曲.WAV"));
        Assert.IsTrue(AudioClip.IsAudioFile("song.mp3"));
        Assert.IsFalse(AudioClip.IsAudioFile("model.pmx"));
    }

    [TestMethod]
    public void BrokenFilesAreRejected()
    {
        string path = Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.ThrowsExactly<FormatException>(() => AudioClip.Open(path));
    }
}
