using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Theater3D.App.Logging;
using Theater3D.App.Rendering;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Viewports;

/// <summary>
/// 方式 B: Direct3D 11 で画面外のテクスチャに描き、Direct3D 9Ex と共有して WPF の D3DImage として表示する。
/// WPF の部品と重ねられ、入力も通常の WPF の部品として受け取れる。
/// 代わりに GPU の描画完了を待ってから WPF に渡すため、方式 A より手間が多い。
/// </summary>
public sealed class D3DImageViewport : FrameworkElement, IRenderTarget, IDisposable
{
    private readonly NativeEngine _engine;
    private readonly RenderLoop _renderLoop;
    private readonly D3DImage _image = new();
    private readonly FrameTimer _frameTimer = new();
    private NativeView? _view;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _backBufferDirty;
    private string? _errorMessage;
    private bool _disposed;

    public D3DImageViewport(NativeEngine engine, RenderLoop renderLoop)
    {
        _engine = engine;
        _renderLoop = renderLoop;
        _image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        _renderLoop.Add(this);
    }

    public bool NeedsRender { get; private set; } = true;

    public double AverageFrameMilliseconds => _frameTimer.AverageMilliseconds;

    public void RenderFrame(float timeSeconds)
    {
        NeedsRender = false;
        if (_view is null || _pixelWidth == 0 || _pixelHeight == 0 || !_image.IsFrontBufferAvailable)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        _image.Lock();
        try
        {
            if (_backBufferDirty)
            {
                // 画面を共有できない環境（リモートデスクトップなど）ではソフトウェアでの写しに切り替える
                _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _view.D3D9Surface, enableSoftwareFallback: true);
                _backBufferDirty = false;
                InvalidateVisual();
            }
            _view.Render(timeSeconds);
            _image.AddDirtyRect(new Int32Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
        }
        finally
        {
            _image.Unlock();
        }
        _frameTimer.Add(Stopwatch.GetElapsedTime(start));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _renderLoop.Remove(this);
        _image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        ReleaseBackBuffer();
        _view?.Dispose();
        _view = null;
        AppLog.Info("方式 B（D3DImage）の描画先を破棄しました");
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(RenderSize);
        if (_errorMessage is not null)
        {
            drawingContext.DrawRectangle(Brushes.Black, null, bounds);
            var text = new FormattedText(
                _errorMessage,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"),
                13,
                Brushes.OrangeRed,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, bounds.Width - 24),
            };
            drawingContext.DrawText(text, new Point(12, 48)); // 左上の重ね表示の札と重ならない位置
            return;
        }
        if (_image.PixelWidth > 0)
        {
            drawingContext.DrawImage(_image, bounds);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdatePixelSize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdatePixelSize();
    }

    private void UpdatePixelSize()
    {
        if (_disposed || _errorMessage is not null)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (width == _pixelWidth && height == _pixelHeight)
        {
            return;
        }
        _pixelWidth = width;
        _pixelHeight = height;

        ReleaseBackBuffer();
        try
        {
            if (width > 0 && height > 0 && _view is null)
            {
                _view = _engine.CreateOffscreenView(width, height, shareWithD3D9: true);
                AppLog.Info("方式 B（D3DImage）の描画先を作りました");
            }
            else
            {
                _view?.Resize(width, height);
            }
        }
        catch (NativeEngineException ex)
        {
            AppLog.Error("方式 B（D3DImage）の描画先を用意できませんでした", ex);
            _errorMessage = $"方式 B（D3DImage）は使えません。\n{ex.Message}";
            _renderLoop.Remove(this);
            InvalidateVisual();
            return;
        }

        _backBufferDirty = true;
        NeedsRender = true;
        _renderLoop.RequestRender();
    }

    private void ReleaseBackBuffer()
    {
        // 古い面を D3DImage が握ったまま作り直すと壊れるため、先に外す
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        _image.Unlock();
    }

    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 画面ロックなどで WPF の描画デバイスが作り直されたら、面を渡し直す
        if (_image.IsFrontBufferAvailable)
        {
            AppLog.Info("D3DImage の表示面が使えるようになりました。描き直します");
            _backBufferDirty = true;
            NeedsRender = true;
            _renderLoop.RequestRender();
        }
        else
        {
            AppLog.Warning("D3DImage の表示面が一時的に使えなくなりました");
        }
    }
}
