using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Theater3D.App.Logging;
using Theater3D.App.Rendering;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Preview;

/// <summary>
/// 3D を表示する面。Direct3D 11 で画面外のテクスチャに描き、Direct3D 9Ex と共有して WPF の D3DImage として出す
/// （方式 B。docs/設計判断記録.md 3 章）。WPF の部品を上に重ねられ、入力も普通の WPF 部品として受け取れる。
/// 共有できない環境（ソフトウェア描画など）では、描いた絵を読み出して WriteableBitmap で出す予備の方式に切り替える。
/// </summary>
public sealed class ViewportSurface : FrameworkElement, IRenderTarget, IDisposable
{
    private readonly EditorContext _context;
    private readonly Func<int, int, FrameParameters> _frameProvider;
    private readonly D3DImage _sharedImage = new();
    private WriteableBitmap? _readbackImage;
    private NativeView? _view;
    private bool _useReadback;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _backBufferDirty;
    private bool _locked;
    private string? _errorMessage;
    private bool _disposed;

    public ViewportSurface(EditorContext context, Func<int, int, FrameParameters> frameProvider)
    {
        _context = context;
        _frameProvider = frameProvider;
        _useReadback = context.SurfaceSharingUnavailable;
        _sharedImage.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Invalidate();
                _context.RenderLoop.RequestRender();
            }
        };
        if (context.Engine is null)
        {
            _errorMessage = "描画エンジンを使えないため、3D を表示できません。";
        }
        else
        {
            context.RenderLoop.Register(this);
        }
    }

    public bool NeedsRender { get; private set; } = true;

    public bool CanRender =>
        !_disposed && _errorMessage is null && IsVisible && _pixelWidth > 0 && _pixelHeight > 0
        && (_useReadback || _sharedImage.IsFrontBufferAvailable);

    public int PixelWidth => _pixelWidth;

    public int PixelHeight => _pixelHeight;

    public void Invalidate() => NeedsRender = true;

    public bool Submit()
    {
        NeedsRender = false;
        EnsureView();
        if (_view is null)
        {
            return false;
        }

        FrameParameters frame = _frameProvider(_pixelWidth, _pixelHeight);
        if (_useReadback)
        {
            _view.Render(frame);
            return true;
        }

        _sharedImage.Lock();
        _locked = true;
        try
        {
            if (_backBufferDirty)
            {
                // リモートデスクトップなどで GPU の面を直接出せないときは、WPF がソフトウェアで写す
                _sharedImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _view.D3D9Surface, enableSoftwareFallback: true);
                _backBufferDirty = false;
                InvalidateVisual();
            }
            _view.Render(frame);
        }
        catch
        {
            Unlock();
            throw;
        }
        return true;
    }

    public void Complete(bool succeeded)
    {
        if (_useReadback)
        {
            if (succeeded && _view is not null)
            {
                CopyToReadbackImage();
            }
            return;
        }
        try
        {
            if (succeeded)
            {
                _sharedImage.AddDirtyRect(new Int32Rect(0, 0, _sharedImage.PixelWidth, _sharedImage.PixelHeight));
            }
        }
        finally
        {
            Unlock();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _context.RenderLoop.Unregister(this);
        _sharedImage.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        ReleaseBackBuffer();
        _view?.Dispose();
        _view = null;
    }

    public override string ToString() => $"ViewportSurface({_pixelWidth}x{_pixelHeight})";

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(RenderSize);
        drawingContext.DrawRectangle(Brushes.Black, null, bounds); // 入力を受けるための下地も兼ねる
        if (_errorMessage is not null)
        {
            var text = new FormattedText(
                _errorMessage,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"),
                12,
                Brushes.OrangeRed,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, bounds.Width - 24),
            };
            drawingContext.DrawText(text, new Point(12, 36));
            return;
        }

        ImageSource? image = _useReadback ? _readbackImage : (_sharedImage.PixelWidth > 0 ? _sharedImage : null);
        if (image is not null)
        {
            drawingContext.DrawImage(image, bounds);
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
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (width == _pixelWidth && height == _pixelHeight)
        {
            return;
        }
        _pixelWidth = width;
        _pixelHeight = height;

        if (_view is not null)
        {
            ReleaseBackBuffer();
            try
            {
                _view.Resize(width, height);
            }
            catch (NativeEngineException ex)
            {
                SetError("描画先の大きさを変えられませんでした", ex);
                return;
            }
            _backBufferDirty = true;
            _readbackImage = null;
        }
        Invalidate();
        _context.RenderLoop.RequestRender();
    }

    // 見えていて大きさが決まってから GPU の資源を作る（隠れた区画は資源を持たない）
    private void EnsureView()
    {
        if (_view is not null || _context.Engine is null)
        {
            return;
        }
        _useReadback |= _context.SurfaceSharingUnavailable; // ほかの区画で共有できないと分かっていれば試さない
        if (!_useReadback)
        {
            try
            {
                _view = _context.Engine.CreateOffscreenView(_pixelWidth, _pixelHeight, shareWithD3D9: true);
                _backBufferDirty = true;
                return;
            }
            catch (NativeEngineException ex) when (ex.Result == NativeResult.NotSupported)
            {
                AppLog.Warning($"GPU の面を WPF と共有できないため、描いた絵を読み出して表示する方式に切り替えます: {ex.Message}");
                _context.SurfaceSharingUnavailable = true;
                _useReadback = true;
            }
        }
        try
        {
            _view = _context.Engine.CreateOffscreenView(_pixelWidth, _pixelHeight, shareWithD3D9: false);
        }
        catch (NativeEngineException ex)
        {
            SetError("描画先を用意できませんでした", ex);
        }
    }

    private void CopyToReadbackImage()
    {
        if (_readbackImage is null || _readbackImage.PixelWidth != _pixelWidth || _readbackImage.PixelHeight != _pixelHeight)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            _readbackImage = new WriteableBitmap(_pixelWidth, _pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
            InvalidateVisual();
        }
        _readbackImage.Lock();
        try
        {
            _view!.ReadPixels(_readbackImage.BackBuffer, _readbackImage.BackBufferStride, _readbackImage.BackBufferStride * _pixelHeight);
            _readbackImage.AddDirtyRect(new Int32Rect(0, 0, _pixelWidth, _pixelHeight));
        }
        finally
        {
            _readbackImage.Unlock();
        }
    }

    private void Unlock()
    {
        if (_locked)
        {
            _sharedImage.Unlock();
            _locked = false;
        }
    }

    private void ReleaseBackBuffer()
    {
        // 古い面を D3DImage が握ったまま作り直すと壊れるため、先に外す
        _sharedImage.Lock();
        _sharedImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        _sharedImage.Unlock();
    }

    private void SetError(string message, NativeEngineException ex)
    {
        AppLog.Error(message, ex);
        _errorMessage = $"{message}。\n{ex.Message}";
        _context.RenderLoop.Unregister(this);
        InvalidateVisual();
    }

    private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 画面ロックなどで WPF の描画デバイスが作り直されたら、面を渡し直す
        if (_sharedImage.IsFrontBufferAvailable)
        {
            _backBufferDirty = true;
            Invalidate();
            _context.RenderLoop.RequestRender();
        }
    }
}
