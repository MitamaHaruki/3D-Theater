using System.Windows;
using System.Windows.Media;
using Theater3D.App.Logging;
using Theater3D.App.Preview;
using Theater3D.App.Shell;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの配信用の画面。シーンのカメラから見た絵だけを、窓の中いっぱいに描く（描き方は <see cref="LiveFrameBuilder"/>。仮想カメラと同じ）。
/// OBS などのウィンドウキャプチャで取り込み、背景の色をクロマキーで抜く。絵の大きさは窓の中の大きさ（設定の大きさに合わせて開く。手で変えてもよい）。
/// </summary>
public partial class OutputWindow : Window
{
    /// <summary>窓の置き場所を覚える名前。</summary>
    public const string WindowKey = "LiveOutputWindow";

    private readonly EditorContext _context;
    private readonly ViewportSurface _surface;

    /// <param name="near">初めて開くとき（置き場所を覚えていないとき）に、近くに出す窓（メインの窓と同じ画面に出すため）。</param>
    internal OutputWindow(EditorContext context, Window? near = null)
    {
        InitializeComponent();
        _context = context;
        Icon = AppIcon.CreateBitmap(64);
        _surface = new ViewportSurface(context, new LiveFrameBuilder(context).Build);
        Host.Children.Add(_surface);
        context.LiveOutput.Changed += OnOutputChanged;
        if (WindowMemory.Get(WindowKey) is null && near is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = near.Left + 60;
            Top = near.Top + 60;
        }
        WindowMemory.Track(this, WindowKey);
        Loaded += (_, _) =>
        {
            FitToOutputSize();
            AppLog.Info($"配信用の画面を開きました（{context.LiveOutput.Width} × {context.LiveOutput.Height}）");
        };
        Closed += (_, _) =>
        {
            context.LiveOutput.Changed -= OnOutputChanged;
            _surface.Dispose();
            AppLog.Info("配信用の画面を閉じました");
        };
    }

    /// <summary>今描いている絵の大きさ（画素）。</summary>
    public (int Width, int Height) PixelSize => (_surface.PixelWidth, _surface.PixelHeight);

    /// <summary>窓の中の大きさを、設定の大きさ（画素）に合わせる。</summary>
    public void FitToOutputSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Host.Width = _context.LiveOutput.Width / dpi.DpiScaleX;
        Host.Height = _context.LiveOutput.Height / dpi.DpiScaleY;
        SizeToContent = SizeToContent.WidthAndHeight;
        UpdateLayout();
        // 合わせた後は、手で大きさを変えられるようにする（中の絵は窓に合わせて描く）
        double width = ActualWidth;
        double height = ActualHeight;
        SizeToContent = SizeToContent.Manual;
        Width = width;
        Height = height;
        Host.Width = double.NaN;
        Host.Height = double.NaN;
    }

    /// <summary>拡大率の違う画面へ動かしたら、絵の大きさ（画素）が変わらないよう合わせ直す（配信ソフトで取り込む大きさを保つ）。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(FitToOutputSize, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        if (_context.LiveOutput.Width != _surface.PixelWidth || _context.LiveOutput.Height != _surface.PixelHeight)
        {
            FitToOutputSize();
        }
        _surface.Invalidate();
        _context.RenderLoop.RequestRender();
    }
}
