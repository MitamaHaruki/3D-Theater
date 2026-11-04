using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Theater3D.App.Logging;
using Theater3D.App.Rendering;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Viewports;

/// <summary>
/// 方式 A: ネイティブの子ウィンドウを WPF に埋め込み、そこへ Direct3D 11 で直接描く。
/// 速いが、WPF の部品を 3D の上に重ねられない（いわゆる「エアスペース問題」）。
/// マウスやキーボードの入力も WPF ではなく子ウィンドウに届く。
/// </summary>
public sealed class HwndViewportHost(NativeEngine engine, RenderLoop renderLoop) : HwndHost, IRenderTarget
{
    private readonly FrameTimer _frameTimer = new();
    private NativeView? _view;
    private int _pixelWidth;
    private int _pixelHeight;

    public bool NeedsRender { get; private set; } = true;

    public double AverageFrameMilliseconds => _frameTimer.AverageMilliseconds;

    public void RenderFrame(float timeSeconds)
    {
        NeedsRender = false;
        if (_view is null || _pixelWidth == 0 || _pixelHeight == 0)
        {
            return;
        }
        long start = Stopwatch.GetTimestamp();
        if (!_view.Render(timeSeconds))
        {
            NeedsRender = true; // 表示待ちで描けなかった。次の画面更新で描く
            return;
        }
        _frameTimer.Add(Stopwatch.GetElapsedTime(start));
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _view = engine.CreateWindowView(hwndParent.Handle, _pixelWidth, _pixelHeight);
        AppLog.Info("方式 A（HwndHost）の描画先を作りました");
        renderLoop.Add(this);
        return new HandleRef(this, _view.WindowHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        renderLoop.Remove(this);
        _view?.Dispose();
        _view = null;
        AppLog.Info("方式 A（HwndHost）の描画先を破棄しました");
    }

    // rcBoundingBox は親ウィンドウ内の位置で、単位は実ピクセル
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        int width = Math.Max(0, (int)Math.Round(rcBoundingBox.Width));
        int height = Math.Max(0, (int)Math.Round(rcBoundingBox.Height));
        if (width == _pixelWidth && height == _pixelHeight)
        {
            return;
        }
        _pixelWidth = width;
        _pixelHeight = height;
        _view?.Resize(width, height);
        NeedsRender = true;
        renderLoop.RequestRender();
    }
}
