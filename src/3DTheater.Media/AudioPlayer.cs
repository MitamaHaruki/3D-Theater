using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Theater3D.Media;

/// <summary>
/// 音声を鳴らす（WASAPI の共有モード）。<see cref="Position"/> は、実際に鳴っている位置（音の出る装置の時計で数えたもの）で、
/// 再生位置をこれに合わせると、映像と音がずれていかない。音の出る装置が無いときは <see cref="Open"/> が例外になる。
/// 作ったスレッド（画面のスレッド）から使う。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const int LatencyMilliseconds = 60;

    private readonly WaveStream _reader;
    private readonly WasapiOut _output;
    private TimeSpan _start;
    private long _startBytes;
    private bool _playing;
    private int _expectedStops; // 自分で止めた回数（止めた知らせは後から届くので、その分は「鳴り終えた」と見なさない）

    private AudioPlayer(WaveStream reader, WasapiOut output)
    {
        _reader = reader;
        _output = output;
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>最後まで鳴り終えた（<see cref="Stop"/> で止めたときは出ない）。</summary>
    public event EventHandler? Ended;

    public TimeSpan Duration => _reader.TotalTime;

    /// <summary>鳴っているか。</summary>
    public bool IsPlaying => _playing;

    /// <summary>音量（0〜1）。0 でも鳴らし続ける（再生位置の時計として使うため）。</summary>
    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f);
    }

    /// <summary>実際に鳴っている位置（止まっているときは、最後に鳴らし始めた位置）。</summary>
    public TimeSpan Position
    {
        get
        {
            if (!_playing)
            {
                return _start;
            }
            long played = Math.Max(0, _output.GetPosition() - _startBytes);
            return _start + TimeSpan.FromSeconds(played / (double)_output.OutputWaveFormat.AverageBytesPerSecond);
        }
    }

    public static AudioPlayer Open(string path)
    {
        WaveStream reader = AudioClip.OpenReader(path);
        try
        {
            var output = new WasapiOut(AudioClientShareMode.Shared, true, LatencyMilliseconds);
            try
            {
                output.Init(reader);
            }
            catch
            {
                output.Dispose();
                throw;
            }
            return new AudioPlayer(reader, output);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary><paramref name="from"/> から鳴らす（鳴っていれば、いったん止めてから）。</summary>
    public void Play(TimeSpan from)
    {
        StopOutput();
        TimeSpan start = from < TimeSpan.Zero ? TimeSpan.Zero : from > Duration ? Duration : from;
        _reader.CurrentTime = start;
        _start = start;
        _startBytes = _output.GetPosition(); // 装置の位置が 0 に戻らない場合に備えて、鳴らし始めの位置を覚えておく
        _output.Play();
        _playing = true;
    }

    public void Stop()
    {
        if (_playing)
        {
            _start = Position;
        }
        StopOutput();
    }

    public void Dispose()
    {
        _output.PlaybackStopped -= OnPlaybackStopped;
        StopOutput();
        _output.Dispose();
        _reader.Dispose();
    }

    private void StopOutput()
    {
        if (_output.PlaybackState == PlaybackState.Stopped)
        {
            _playing = false;
            return;
        }
        _expectedStops++;
        try
        {
            _output.Stop();
        }
        finally
        {
            _playing = false;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_expectedStops > 0)
        {
            _expectedStops--;
            return;
        }
        _playing = false;
        _start = Duration; // 最後まで鳴った
        Ended?.Invoke(this, EventArgs.Empty);
    }
}
