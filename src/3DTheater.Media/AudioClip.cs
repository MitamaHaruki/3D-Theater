using NAudio.Wave;

namespace Theater3D.Media;

/// <summary>
/// 音声ファイルの情報（長さ・形式）と、タイムラインに描く波形の大きさ。
/// WAV（PCM・浮動小数）は直接読み、それ以外（MP3・AAC・WMA・FLAC など）は Windows の Media Foundation で読む。
/// </summary>
public sealed class AudioClip
{
    /// <summary>波形の大きさを、1 秒あたりいくつに分けて覚えるか。</summary>
    public const int PeaksPerSecond = 100;

    private AudioClip(string path, TimeSpan duration, int sampleRate, int channels)
    {
        FilePath = path;
        Duration = duration;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public string FilePath { get; }

    public TimeSpan Duration { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>1/100 秒ごとの、いちばん大きな振れ幅（0〜1）。<see cref="ComputePeaks"/> で求めるまでは空。</summary>
    public float[] Peaks { get; set; } = [];

    /// <summary>形式と長さだけを読む（中身は読まないので速い）。読めなければ例外。</summary>
    public static AudioClip Open(string path)
    {
        using WaveStream reader = OpenReader(path);
        return new AudioClip(path, reader.TotalTime, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    }

    /// <summary>
    /// 全体を読んで、波形の大きさを求める（時間がかかるので、別のスレッドで呼んでよい）。結果を返すだけで、<see cref="Peaks"/> には入れない。
    /// </summary>
    public float[] ComputePeaks(CancellationToken cancel = default)
    {
        using WaveStream reader = OpenReader(FilePath);
        ISampleProvider samples = reader.ToSampleProvider();
        int channels = samples.WaveFormat.Channels;
        int perPeak = Math.Max(1, samples.WaveFormat.SampleRate / PeaksPerSecond) * channels;
        var peaks = new List<float>((int)(Duration.TotalSeconds * PeaksPerSecond) + 2);
        var buffer = new float[perPeak * 64];
        float current = 0;
        int counted = 0;
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                float a = Math.Abs(buffer[i]);
                if (a > current)
                {
                    current = a;
                }
                if (++counted == perPeak)
                {
                    peaks.Add(Math.Min(current, 1f));
                    current = 0;
                    counted = 0;
                }
            }
        }
        if (counted > 0)
        {
            peaks.Add(Math.Min(current, 1f));
        }
        return [.. peaks];
    }

    /// <summary>この形式を読めるか（拡張子で決める）。</summary>
    public static bool IsAudioFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".mp3" or ".m4a" or ".aac" or ".wma" or ".flac";

    internal static WaveStream OpenReader(string path)
    {
        if (System.IO.Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wave = new WaveFileReader(path);
            if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
            {
                return wave;
            }
            wave.Dispose(); // ADPCM などは Media Foundation に任せる
        }
        return new MediaFoundationReader(path);
    }
}
