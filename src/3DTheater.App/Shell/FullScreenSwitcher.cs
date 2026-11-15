using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Theater3D.App.Logging;

namespace Theater3D.App.Shell;

/// <summary>
/// ライブモードの窓の、標準のタイトルバーと仮想フルスクリーン（F11）（設計判断記録 78 章）。
/// 仮想フルスクリーンは、タイトルバーと枠を消して、窓のある画面いっぱいに広げる（画面の解像度は変えない）。
/// 戻すときは、入る前の置き場所（元の大きさのときの位置・最大化）に戻す。
/// </summary>
internal sealed partial class FullScreenSwitcher(Window window)
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private WindowPlacementInfo _saved;
    private WindowStyle _style;
    private ResizeMode _resizeMode;

    /// <summary>仮想フルスクリーンにしているか。</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>標準のタイトルバーを、アプリの色に合わせて暗くする（窓ができたとき）。</summary>
    public static void UseDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
    }

    public void Toggle()
    {
        if (IsFullScreen)
        {
            Exit();
        }
        else
        {
            Enter();
        }
    }

    public void Enter()
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacementInfo { Length = Marshal.SizeOf<WindowPlacementInfo>() };
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (IsFullScreen || hwnd == 0 || !GetWindowPlacement(hwnd, ref placement)
            || !GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref monitor))
        {
            return;
        }
        _saved = placement;
        _style = window.WindowStyle;
        _resizeMode = window.ResizeMode;
        IsFullScreen = true;
        // 最大化のままだと、枠を消しても作業領域（タスクバーの外）にしか広がらないので、元の大きさにしてから広げる
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        NativeRect r = monitor.Monitor;
        SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        AppLog.Info($"仮想フルスクリーンにしました（{r.Right - r.Left} × {r.Bottom - r.Top}）");
    }

    public void Exit()
    {
        if (!IsFullScreen)
        {
            return;
        }
        IsFullScreen = false;
        window.WindowStyle = _style;
        window.ResizeMode = _resizeMode;
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != 0)
        {
            WindowPlacementInfo placement = _saved;
            placement.Flags = 0;
            SetWindowPlacement(hwnd, ref placement); // 元の位置・大きさと、最大化していたかを戻す
        }
        AppLog.Info("仮想フルスクリーンから戻しました");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacementInfo
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hwnd, ref WindowPlacementInfo placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint hwnd, ref WindowPlacementInfo placement);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
