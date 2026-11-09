using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Media;

namespace Theater3D.App.Audio;

/// <summary>
/// シーンの音声（<see cref="AudioNode"/>）を、再生に合わせて鳴らす。再生中は、鳴っている位置を再生位置の時計にする
/// （<see cref="IPlaybackClock"/>。映像と音がずれていかないように、再生時刻を 1 か所で決める）。
/// - 再生を始めたら、今のフレームにあたる位置から鳴らす。止めたら止める
/// - ループで先頭へ戻った・再生中に位置を飛ばしたときは、その位置から鳴らし直す
/// - 鳴り始めのフレームより前と、音声が終わった後は、今までどおり実際の時間で進める
/// - 表示を切る（音を消す）と音量 0 で流し続ける（タイミングを変えないため）
/// 音の出る装置が無いときは、音なしで再生する（時計は実際の時間）。波形は別のスレッドで求める。
/// </summary>
public sealed class AudioService : IPlaybackClock, IDisposable
{
    /// <summary>鳴っている位置と再生位置がこれ以上ずれたら、鳴らし直す（フレーム）。</summary>
    private const double ResyncFrames = 3;

    private readonly EditorContext _context;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, float[]> _peaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _computing = new(StringComparer.OrdinalIgnoreCase);
    private AudioNode? _node;
    private AudioPlayer? _player;
    private bool _wasPlaying;
    private int _frameRate;

    internal AudioService(EditorContext context)
    {
        _context = context;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _frameRate = context.Playback.FrameRate;
        context.Playback.Clock = this;
        context.Scene.NodeChanged += OnNodeChanged;
        context.Playback.StateChanged += (_, _) => OnPlaybackStateChanged();
        context.Playback.FrameChanged += (_, _) => OnFrameChanged();
    }

    /// <summary>波形を求め終えた（タイムラインを描き直す）。</summary>
    public event EventHandler? WaveformChanged;

    /// <summary>音を鳴らせる（装置があり、ファイルを開けた）。</summary>
    public bool CanPlaySound => _player is not null;

    /// <summary>波形の大きさ（1/100 秒ごと。<see cref="AudioClip.PeaksPerSecond"/>）。まだ求めていなければ null。</summary>
    public float[]? PeaksFor(AudioNode node) => _peaks.GetValueOrDefault(node.FilePath);

    public bool TryGetFramePosition(out double frame)
    {
        frame = 0;
        if (_player is not { IsPlaying: true } || _node is null)
        {
            return false;
        }
        frame = _node.StartFrame + (_player.Position.TotalSeconds * _context.Playback.FrameRate);
        return true;
    }

    public void Dispose()
    {
        _context.Playback.Clock = null;
        _player?.Dispose();
        _player = null;
    }

    private void OnNodeChanged(object? sender, SceneNodeChangedEventArgs e)
    {
        if (e.Node == _context.Scene.AudioGroup && e.PropertyName == nameof(SceneNode.Children))
        {
            SyncNode();
        }
        else if (_node is not null && (e.Node == _node || e.Node == _context.Scene.AudioGroup) && e.PropertyName == nameof(SceneNode.IsVisible))
        {
            ApplyVolume();
        }
        else if (e.Node == _node && e.PropertyName == nameof(AudioNode.Volume))
        {
            ApplyVolume();
        }
        else if (e.Node == _node && e.PropertyName == nameof(AudioNode.StartFrame) && _context.Playback.IsPlaying)
        {
            StartAt(_context.Playback.CurrentFrame); // 鳴り始めをずらしたら、今の位置から鳴らし直す
        }
    }

    // シーンの音声が入れ替わったら、鳴らすものも入れ替える
    private void SyncNode()
    {
        AudioNode? next = _context.Scene.Audio;
        if (ReferenceEquals(next, _node))
        {
            return;
        }
        _player?.Dispose();
        _player = null;
        _node = next;
        if (next is null)
        {
            return;
        }
        EnsurePeaks(next);
        try
        {
            _player = AudioPlayer.Open(next.FilePath);
            ApplyVolume();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning($"音声「{next.Name}」を鳴らせません（音の出る装置が無いか、ファイルを読めません）。再生は音なしで進めます", ex);
            return;
        }
        if (_context.Playback.IsPlaying)
        {
            StartAt(_context.Playback.CurrentFrame);
        }
    }

    private void ApplyVolume()
    {
        if (_player is not null && _node is not null)
        {
            _player.Volume = _node.IsEffectivelyVisible ? _node.Volume : 0f;
        }
    }

    private void OnPlaybackStateChanged()
    {
        PlaybackService playback = _context.Playback;
        bool playing = playback.IsPlaying;
        if (playing && (!_wasPlaying || playback.FrameRate != _frameRate))
        {
            StartAt(playback.CurrentFrame);
        }
        else if (!playing && _wasPlaying)
        {
            Stop();
        }
        _wasPlaying = playing;
        _frameRate = playback.FrameRate;
    }

    private void OnFrameChanged()
    {
        PlaybackService playback = _context.Playback;
        if (!playback.IsPlaying || _player is null || _node is null)
        {
            return;
        }
        int frame = playback.CurrentFrame;
        if (_player.IsPlaying)
        {
            double heard = _node.StartFrame + (_player.Position.TotalSeconds * playback.FrameRate);
            if (Math.Abs(frame - heard) > ResyncFrames)
            {
                StartAt(frame); // ループで先頭へ戻った・再生中に位置を飛ばした
            }
        }
        else if (frame >= _node.StartFrame && frame < _node.EndFrame(playback.FrameRate))
        {
            StartAt(frame); // 鳴り始めのフレームに来た
        }
    }

    private void StartAt(int frame)
    {
        if (_player is null || _node is null)
        {
            return;
        }
        double seconds = (frame - _node.StartFrame) / (double)_context.Playback.FrameRate;
        if (seconds >= 0 && seconds < _player.Duration.TotalSeconds)
        {
            _player.Play(TimeSpan.FromSeconds(seconds));
        }
        else if (_player.IsPlaying)
        {
            _player.Stop();
        }
    }

    private void Stop()
    {
        if (_player is not { IsPlaying: true } || _node is null)
        {
            _player?.Stop();
            return;
        }
        // 止めたときに、鳴っていた位置と表示していたフレームのずれを記録しておく（同期の確認用）
        double heard = _node.StartFrame + (_player.Position.TotalSeconds * _context.Playback.FrameRate);
        _player.Stop();
        AppLog.Info($"音声を止めました（{_player.Position.TotalSeconds:F3} 秒 = フレーム {heard:F1}、表示していたフレーム {_context.Playback.CurrentFrame}）");
    }

    // 波形は全体を読むので、別のスレッドで求める
    private void EnsurePeaks(AudioNode node)
    {
        string path = node.FilePath;
        if (_peaks.ContainsKey(path) || !_computing.Add(path))
        {
            return;
        }
        Task.Run(() => AudioClip.Open(path).ComputePeaks()).ContinueWith(task =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                _computing.Remove(path);
                if (task.IsCompletedSuccessfully)
                {
                    _peaks[path] = task.Result;
                    WaveformChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    AppLog.Warning($"音声の波形を求められませんでした: {path}", task.Exception?.GetBaseException());
                }
            });
        }, TaskScheduler.Default);
    }
}
