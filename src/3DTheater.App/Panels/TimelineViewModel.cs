using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>タイムラインの上の操作欄（再生ボタン・フレーム番号・ループ・フレームレート）。</summary>
public sealed class TimelineViewModel : ObservableObject
{
    private readonly PlaybackService _playback;

    public TimelineViewModel(PlaybackService playback)
    {
        _playback = playback;
        playback.FrameChanged += (_, _) => OnPropertyChanged(nameof(CurrentFrame));
        playback.StateChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

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

    public double EndFrame
    {
        get => _playback.EndFrame;
        set
        {
            _playback.EndFrame = (int)Math.Round(value);
            OnPropertyChanged(nameof(EndFrame));
        }
    }

    public bool Loop
    {
        get => _playback.Loop;
        set => _playback.Loop = value;
    }

    public int FrameRate
    {
        get => _playback.FrameRate;
        set => _playback.FrameRate = value;
    }

    public string PlayGlyph => _playback.IsPlaying ? "" : "";

    public string PlayToolTip => _playback.IsPlaying ? "一時停止（Space）" : "再生（Space）";
}
