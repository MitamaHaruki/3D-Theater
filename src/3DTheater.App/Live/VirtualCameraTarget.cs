using System.Diagnostics;
using Theater3D.App.Logging;
using Theater3D.App.Preview;
using Theater3D.App.Rendering;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Media;

namespace Theater3D.App.Live;

/// <summary>
/// 仮想カメラへ送る絵を描く（描画ループの描画先の 1 つ）。カメラの元に絵を頼まれている間だけ、頼まれた大きさで 1 秒に 30 回、
/// 配信用の画面と同じ絵（<see cref="LiveFrameBuilder"/>）を描く。描いた絵は、描画ループが次の画面更新で GPU の完了を待った後に読み出し、
/// <see cref="VirtualCameraFrames"/> に置く（読み出しは、ほとんど待たない）。
/// </summary>
internal sealed class VirtualCameraTarget : IRenderTarget, IDisposable
{
    private static readonly long Interval = Stopwatch.Frequency / VirtualCameraProtocol.FrameRate;

    // 予定より少し早い画面の更新でも描く（更新の時刻の揺れの分。2 ms）
    private static readonly long Slack = Stopwatch.Frequency / 500;

    private readonly EditorContext _context;
    private readonly VirtualCameraFrames _frames;
    private readonly LiveFrameBuilder _builder;
    private NativeView? _view;
    private int _width;
    private int _height;
    private int _sampleCount = 1;
    private bool _pending;
    private long _nextDue;
    private bool _disposed;

    public VirtualCameraTarget(EditorContext context, VirtualCameraFrames frames)
    {
        _context = context;
        _frames = frames;
        _builder = new LiveFrameBuilder(context);
        if (context.Engine is { } engine)
        {
            engine.DeviceResetting += OnDeviceResetting;
        }
        context.PreviewQuality.Changed += OnPreviewQualityChanged;
        context.RenderLoop.Register(this);
    }

    /// <summary>描いた回数（確かめ用）。</summary>
    public long FramesRendered { get; private set; }

    public bool CanRender => !_disposed && _context.Engine is not null && _frames.TryGetWanted(out _, out _);

    // 時間で描く（1 秒に 30 回）。ライブモードは描画ループを止めないので、毎回の画面更新で確かめる。
    // 次の予定は、描いた時刻からではなく、前の予定から 1/30 秒ずつ進める（描いた時刻から数えると、画面の更新が 1 秒に 75 回のときは
    // 3 回に 1 回（25 回/秒）、60 回のときも時刻の揺れで 2 回か 3 回に 1 回しか描けない。速さのログで分かった）
    public bool NeedsRender => Stopwatch.GetTimestamp() >= _nextDue - Slack;

    public void Invalidate()
    {
    }

    public bool HasPendingFrame => _pending;

    public bool PresentImmediately => false;

    public double? GpuMilliseconds => _view is { IsDisposed: false } view ? view.GpuMilliseconds : null;

    public bool Submit()
    {
        if (!_frames.TryGetWanted(out int width, out int height))
        {
            return false;
        }
        EnsureView(width, height);
        if (_view is null)
        {
            return false;
        }
        long now = Stopwatch.GetTimestamp();
        _nextDue = now - _nextDue > Interval ? now + Interval : _nextDue + Interval; // 遅れすぎたら（頼まれていなかったなど）、今から数え直す
        _view.Render(_builder.Build(width, height));
        _pending = true;
        return true;
    }

    public void Publish() => _view?.Publish();

    public void Complete(bool succeeded)
    {
        _pending = false;
        if (!succeeded || _view is null || _disposed)
        {
            return;
        }
        byte[] buffer = _frames.GetWriteBuffer(_width, _height);
        _view.ReadPixels(buffer, _width * 4);
        _frames.Publish(_width, _height);
        FramesRendered++;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _context.RenderLoop.Unregister(this);
        if (_context.Engine is { } engine)
        {
            engine.DeviceResetting -= OnDeviceResetting;
        }
        _context.PreviewQuality.Changed -= OnPreviewQualityChanged;
        _view?.Dispose();
        _view = null;
    }

    private void EnsureView(int width, int height)
    {
        if (_view is not null && width == _width && height == _height)
        {
            return;
        }
        if (_view is null)
        {
            // 描く面と読み出す面を分ける（描いた絵は Publish で写し、GPU の完了を待ってから読む）
            _view = _context.Engine!.CreateOffscreenView(width, height, shareWithD3D9: false, deferredPublish: true);
            _sampleCount = 1;
            AppLog.Info($"仮想カメラの絵を {width} × {height} で描き始めます");
        }
        else
        {
            _view.Resize(width, height);
            _pending = false;
            AppLog.Info($"仮想カメラの絵の大きさを {width} × {height} にしました");
        }
        _width = width;
        _height = height;
        ApplySampleCount();
    }

    // 線や輪郭のなめらかさは、プレビューの品質に合わせる（配信用の画面と同じ）
    private void ApplySampleCount()
    {
        if (_view is null)
        {
            return;
        }
        int wanted = RenderQuality.FitSampleCount(_context.PreviewQuality.Profile.SampleCount, _width, _height);
        if (wanted == _sampleCount)
        {
            return;
        }
        try
        {
            _view.SetSampleCount(wanted);
            _sampleCount = wanted;
        }
        catch (NativeEngineException ex) when (ex.Result != NativeResult.DeviceLost)
        {
            _sampleCount = 1;
            AppLog.Warning("仮想カメラの絵を、なめらかにせずに描きます", ex);
        }
    }

    private void OnPreviewQualityChanged(object? sender, EventArgs e) => ApplySampleCount();

    private void OnDeviceResetting(object? sender, EventArgs e) => _pending = false;
}
