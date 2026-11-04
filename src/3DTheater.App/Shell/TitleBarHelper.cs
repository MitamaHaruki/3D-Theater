using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Theater3D.App.Shell;

/// <summary>
/// メニューを入れた自前のタイトルバーを、Windows 標準のタイトルバーと同じように動かすための処理。
/// - 最大化ボタンの上で Windows 11 のスナップレイアウトが出るよう、OS には「最大化ボタン」と答える
/// - 最大化したときに画面の外へはみ出す枠の分だけ、中身を内側へ寄せる
/// - 枠の色を暗い配色に合わせる
/// WPF の WindowChrome がタイトルバーの判定を先に済ませてしまうため、ウィンドウをサブクラス化して
/// WPF より手前でメッセージを受け取る。
/// </summary>
internal sealed partial class TitleBarHelper
{
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_NCMOUSEMOVE = 0x00A0;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint WM_NCLBUTTONUP = 0x00A2;
    private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    private const uint WM_NCMOUSELEAVE = 0x02A2;
    private const uint WM_NCDESTROY = 0x0082;
    private const int HTMAXBUTTON = 9;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint TME_LEAVE = 0x2;
    private const uint TME_NONCLIENT = 0x10;
    private const nuint SubclassId = 0x3D7;

    // サブクラスの関数はネイティブ側から呼ばれるので、ウィンドウがある間は参照を持っておく
    private static readonly List<TitleBarHelper> Alive = [];

    private readonly Window _window;
    private readonly Button _maximizeButton;
    private readonly FrameworkElement _root;
    private readonly SubclassProc _proc;
    private bool _pressed;

    private TitleBarHelper(Window window, Button maximizeButton, FrameworkElement root)
    {
        _window = window;
        _maximizeButton = maximizeButton;
        _root = root;
        _proc = WindowProc;
    }

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData);

    public static void Attach(Window window, Button maximizeButton, FrameworkElement root)
    {
        var helper = new TitleBarHelper(window, maximizeButton, root);
        window.SourceInitialized += (_, _) => helper.OnSourceInitialized();
        window.StateChanged += (_, _) => helper.UpdateMaximized();
        window.DpiChanged += (_, _) => helper.UpdateMaximized();
    }

    private void OnSourceInitialized()
    {
        nint hwnd = new WindowInteropHelper(_window).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int borderColor = 0x00342D2A; // COLORREF は 0x00BBGGRR（#2A2D34）
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        if (SetWindowSubclass(hwnd, Marshal.GetFunctionPointerForDelegate(_proc), SubclassId, 0))
        {
            Alive.Add(this);
        }
        UpdateMaximized();
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaximizeButton(lParam))
                {
                    _maximizeButton.Tag = _pressed ? "Pressed" : "Hover";
                    return HTMAXBUTTON;
                }
                ClearState();
                break;
            case WM_NCMOUSEMOVE when wParam == HTMAXBUTTON:
                // ウィンドウの外へ抜けたときに WM_NCMOUSELEAVE で知らせてもらう
                var track = new TrackMouseEventInfo
                {
                    Size = (uint)Marshal.SizeOf<TrackMouseEventInfo>(),
                    Flags = TME_LEAVE | TME_NONCLIENT,
                    Window = hwnd,
                };
                TrackMouseEvent(ref track);
                break;
            case WM_NCMOUSELEAVE:
                ClearState();
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam == HTMAXBUTTON:
                _pressed = true;
                _maximizeButton.Tag = "Pressed";
                return 0;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                if (_pressed)
                {
                    _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                ClearState();
                return 0;
            case WM_NCDESTROY:
                RemoveWindowSubclass(hwnd, Marshal.GetFunctionPointerForDelegate(_proc), SubclassId);
                Alive.Remove(this);
                break;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ClearState()
    {
        _pressed = false;
        if (_maximizeButton.Tag is not null)
        {
            _maximizeButton.Tag = null;
        }
    }

    private void UpdateMaximized()
    {
        bool maximized = _window.WindowState == WindowState.Maximized;
        _maximizeButton.Content = maximized ? "" : "";
        _maximizeButton.ToolTip = maximized ? "元のサイズに戻す" : "最大化";
        if (!maximized)
        {
            _root.Margin = new Thickness(0);
            return;
        }
        nint hwnd = new WindowInteropHelper(_window).Handle;
        uint dpi = hwnd == 0 ? 96 : GetDpiForWindow(hwnd);
        int framePixels = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        _root.Margin = new Thickness(framePixels * 96.0 / Math.Max(dpi, 96u));
    }

    private bool IsOverMaximizeButton(nint lParam)
    {
        if (!_maximizeButton.IsVisible || PresentationSource.FromVisual(_maximizeButton) is null)
        {
            return false;
        }
        int x = unchecked((short)(lParam & 0xFFFF));
        int y = unchecked((short)((lParam >> 16) & 0xFFFF));
        Point local = _maximizeButton.PointFromScreen(new Point(x, y));
        return local.X >= 0 && local.Y >= 0 && local.X < _maximizeButton.ActualWidth && local.Y < _maximizeButton.ActualHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public uint Size;
        public uint Flags;
        public nint Window;
        public uint HoverTime;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, nint proc, nuint id, nuint refData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, nint proc, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
}
