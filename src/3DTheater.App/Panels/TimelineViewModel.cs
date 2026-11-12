using System.Windows.Threading;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>タイムラインの上の操作欄（再生ボタン・フレーム番号・ループ・フレームレート）。</summary>
public sealed class TimelineViewModel : ObservableObject
{
    private readonly PlaybackService _playback;
    private readonly AnimationService _animation;
    private readonly EditService _edits;
    private readonly Action<int> _changeFrameRate;

    /// <param name="edits">終わりのフレーム・ループを元に戻せるように変える。</param>
    /// <param name="changeFrameRate">フレームの速さを選んだときに呼ぶ（キーがあれば位置を合わせるか尋ね、元に戻せるように変える）。</param>
    public TimelineViewModel(PlaybackService playback, AnimationService animation, EditService edits, Action<int> changeFrameRate)
    {
        _playback = playback;
        _animation = animation;
        _edits = edits;
        _changeFrameRate = changeFrameRate;
        playback.FrameChanged += (_, _) => OnPropertyChanged(nameof(CurrentFrame));
        playback.StateChanged += (_, _) => OnPropertyChanged(string.Empty);
        animation.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AutoKey));
            OnPropertyChanged(nameof(HasUnregistered));
            OnPropertyChanged(nameof(UnregisteredText));
        };
    }

    /// <summary>自動キーフレーム（初期値は OFF）。</summary>
    public bool AutoKey
    {
        get => _animation.AutoKey;
        set => _animation.AutoKey = value;
    }

    public bool HasUnregistered => _animation.UnregisteredCount > 0;

    public string UnregisteredText => $"未登録の変更 {_animation.UnregisteredCount} 件";

    public IReadOnlyList<int> FrameRates => PlaybackService.SupportedFrameRates;

    public double CurrentFrame
    {
        get => _playback.CurrentFrame;
        set
        {
            _playback.Pause();
            _playback.Seek((int)Math.Round(value));
            OnPropertyChanged(nameof(CurrentFrame));
        }
    }

    /// <summary>終わりのフレーム（元に戻せる。欄のホイール・ドラッグで続けて変えた分は 1 つの操作にまとめる）。</summary>
    public double EndFrame
    {
        get => _playback.EndFrame;
        set
        {
            _edits.ChangeValue("終わりのフレームの変更", () => _playback.EndFrame, v => _playback.EndFrame = v, (int)Math.Round(value),
                mergeKey: "playback.endFrame");
            OnPropertyChanged(nameof(EndFrame));
        }
    }

    /// <summary>ループ再生（元に戻せる）。</summary>
    public bool Loop
    {
        get => _playback.Loop;
        set => _edits.ChangeValue($"ループ再生を {(value ? "ON" : "OFF")} にする", () => _playback.Loop, v => _playback.Loop = v, value);
    }

    public int FrameRate
    {
        get => _playback.FrameRate;
        set
        {
            if (value == _playback.FrameRate)
            {
                return;
            }
            // 選んだ後、一覧が閉じてから尋ねる。やめたときは一覧の表示を今の値へ戻す
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                _changeFrameRate(value);
                OnPropertyChanged(nameof(FrameRate));
            });
        }
    }

    public string PlayGlyph => _playback.IsPlaying ? "" : "";

    public string PlayToolTip => _playback.IsPlaying ? "一時停止（Space）" : "再生（Space）";
}
