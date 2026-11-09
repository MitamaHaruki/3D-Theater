namespace Theater3D.Editor;

/// <summary>再生位置を決める外の時計（音声など）。</summary>
public interface IPlaybackClock
{
    /// <summary>今の再生位置（フレーム。端数あり）を答えられれば true。</summary>
    bool TryGetFramePosition(out double frame);
}

/// <summary>
/// 再生位置（フレーム）と再生状態。プレビュー・タイムライン・書き出しが同じ位置を見るための唯一の持ち主。
/// 時間の進みは外から <see cref="Advance"/> で与える（画面の更新に合わせるため、またテストしやすくするため）。
/// </summary>
public sealed class PlaybackService
{
    public static IReadOnlyList<int> SupportedFrameRates { get; } = [24, 30, 60];

    private int _frameRate = 30;
    private int _endFrame = 300;
    private int _currentFrame;
    private double _subFrame; // 再生中に次のフレームへ進むまでの端数
    private bool _isPlaying;
    private bool _loop = true;

    /// <summary>表示するフレームが変わった。</summary>
    public event EventHandler? FrameChanged;

    /// <summary>再生・停止、ループ、フレームレート、長さのいずれかが変わった。</summary>
    public event EventHandler? StateChanged;

    public int StartFrame => 0;

    public int EndFrame
    {
        get => _endFrame;
        set
        {
            int clamped = Math.Max(1, value);
            if (clamped == _endFrame)
            {
                return;
            }
            _endFrame = clamped;
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (_currentFrame > _endFrame)
            {
                Seek(_endFrame);
            }
        }
    }

    /// <summary>1 秒あたりのフレーム数。変えてもフレーム番号は保つ（キーはフレーム単位のため）。</summary>
    public int FrameRate
    {
        get => _frameRate;
        set
        {
            int clamped = Math.Clamp(value, 1, 240);
            if (clamped == _frameRate)
            {
                return;
            }
            _frameRate = clamped;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int CurrentFrame => _currentFrame;

    public bool IsPlaying => _isPlaying;

    public bool Loop
    {
        get => _loop;
        set
        {
            if (_loop == value)
            {
                return;
            }
            _loop = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Play()
    {
        if (_isPlaying)
        {
            return;
        }
        if (_currentFrame >= _endFrame)
        {
            SetFrame(StartFrame);
        }
        _subFrame = 0;
        _isPlaying = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!_isPlaying)
        {
            return;
        }
        _isPlaying = false;
        _subFrame = 0;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlay()
    {
        if (_isPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>止めて先頭へ戻す。</summary>
    public void Stop()
    {
        Pause();
        Seek(StartFrame);
    }

    public void Seek(int frame)
    {
        _subFrame = 0;
        SetFrame(Math.Clamp(frame, StartFrame, _endFrame));
    }

    public void Step(int frames) => Seek(_currentFrame + frames);

    public void GoToStart() => Seek(StartFrame);

    public void GoToEnd() => Seek(_endFrame);

    /// <summary>
    /// 再生中の位置を決める外の時計（音声）。時計が位置を答えれば、その位置へ進める（実際の時間を足していかない）。
    /// 答えないとき（音声の範囲の外など）は、今までどおり実際の時間で進める。再生時刻を 1 か所で決めるため（指示書 3.1）。
    /// </summary>
    public IPlaybackClock? Clock { get; set; }

    public void Advance(TimeSpan elapsed)
    {
        if (!_isPlaying || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        int steps;
        if (Clock?.TryGetFramePosition(out double position) == true && double.IsFinite(position))
        {
            // 時計が少し遅れて見えても、戻さない（鳴らし始めの直後など）
            steps = (int)Math.Floor(position + 1e-4) - _currentFrame;
            _subFrame = 0;
            if (steps <= 0)
            {
                return;
            }
        }
        else
        {
            _subFrame += elapsed.TotalSeconds * _frameRate;
            // 時間の切り捨て（1 tick = 100ns）で 3.99999… のようになっても 1 フレーム遅れないよう、わずかな誤差は許す
            steps = (int)Math.Floor(_subFrame + 1e-4);
            if (steps == 0)
            {
                return;
            }
            _subFrame -= steps;
        }

        int next = _currentFrame + steps;
        if (next > _endFrame)
        {
            if (_loop)
            {
                int length = _endFrame - StartFrame + 1;
                next = StartFrame + ((next - StartFrame) % length);
            }
            else
            {
                next = _endFrame;
                SetFrame(next);
                Pause();
                return;
            }
        }
        SetFrame(next);
    }

    private void SetFrame(int frame)
    {
        if (frame == _currentFrame)
        {
            return;
        }
        _currentFrame = frame;
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }
}
