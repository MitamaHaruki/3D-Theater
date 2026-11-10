using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Theater3D.Media;

/// <summary>
/// 動画の書き出し用に、音声のファイルを 48 kHz・2 チャンネル・16 ビットにそろえて、決まった時刻から読み出す。
/// <paramref name="startSeconds"/> は動画の 0 秒が音声のどこに当たるか（負なら、その分だけ頭に無音を入れる）。
/// 音声が終わった後は無音を返す。音量を掛けて、16 ビットの範囲に収める。
/// </summary>
public sealed class AudioExportSource : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    private readonly WaveStream _reader;
    private readonly ISampleProvider _samples;
    private readonly float _volume;
    private long _silenceFrames;
    private float[] _buffer = [];
    private bool _ended;

    public AudioExportSource(string path, double startSeconds, float volume)
    {
        _volume = float.IsFinite(volume) ? Math.Max(0f, volume) : 1f;
        _reader = AudioClip.OpenReader(path);
        try
        {
            if (startSeconds >= _reader.TotalTime.TotalSeconds)
            {
                _ended = true; // 音声が終わった後から書き出す
            }
            else if (startSeconds > 0)
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(startSeconds);
            }
            else
            {
                _silenceFrames = (long)Math.Round(-startSeconds * SampleRate);
            }
            ISampleProvider samples = _reader.ToSampleProvider();
            if (samples.WaveFormat.Channels == 1)
            {
                samples = new MonoToStereoSampleProvider(samples);
            }
            else if (samples.WaveFormat.Channels > 2)
            {
                samples = new MultiplexingSampleProvider([samples], 2); // 3 チャンネル以上は、最初の 2 つを左右にする
            }
            if (samples.WaveFormat.SampleRate != SampleRate)
            {
                samples = new WdlResamplingSampleProvider(samples, SampleRate);
            }
            _samples = samples;
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <summary>続きを <paramref name="destination"/> いっぱいに読む（左右を交互。足りない分は無音）。</summary>
    public void Read(Span<short> destination)
    {
        int i = 0;
        while (i + 1 < destination.Length && _silenceFrames > 0)
        {
            destination[i] = destination[i + 1] = 0;
            i += 2;
            _silenceFrames--;
        }
        int need = destination.Length - i;
        if (need > 0 && !_ended)
        {
            if (_buffer.Length < need)
            {
                _buffer = new float[need];
            }
            int read = 0;
            while (read < need)
            {
                int r = _samples.Read(_buffer, read, need - read);
                if (r == 0)
                {
                    _ended = true;
                    break;
                }
                read += r;
            }
            for (int k = 0; k < read; k++)
            {
                destination[i + k] = (short)Math.Clamp(MathF.Round(_buffer[k] * _volume * 32767f), short.MinValue, short.MaxValue);
            }
            i += read;
        }
        destination[i..].Clear();
    }

    public void Dispose() => _reader.Dispose();
}
