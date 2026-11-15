using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Theater3D.App.Shell;

/// <summary>
/// ウィンドウの置き場所（元の大きさのときの位置と大きさ・最大化）。Windows の GetWindowPlacement と同じ画素の座標
/// （作業領域の座標。拡大率によらない）で持つので、拡大率の違うモニターでもずれない。
/// </summary>
public sealed record SavedWindowPlacement(int Left, int Top, int Right, int Bottom, bool Maximized);

/// <summary>
/// ウィンドウの位置と大きさを、閉じるときに覚えて、次に開いたとき（次の起動も）に戻す。
/// - メインのウィンドウ: 位置・大きさ・最大化（最大化は、元の位置に置いてから行う。先に最大化すると、別のモニターに出ることがある）
/// - 設定などの画面: 種類ごとに位置（大きさを変えられる画面は大きさも）
/// - 覚えた位置のタイトルバーが、どのモニターにも出ないとき（モニターを外したなど）は戻さず、初めの位置に開く
/// 覚えた値は画面の設定（<see cref="UserSettings.Windows"/>）として、アプリを閉じるときに保存する。
/// </summary>
internal static partial class WindowMemory
{
    /// <summary>メインのウィンドウの名前。</summary>
    public const string MainWindowKey = "MainWindow";

    /// <summary>ライブモードのメインのウィンドウの名前（エディットとは別の置き場所を覚える）。</summary>
    public const string LiveWindowKey = "LiveWindow";

    private const int SW_HIDE = 0;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 2;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    private static Dictionary<string, SavedWindowPlacement> _placements = [];

    /// <summary>保存してある置き場所を読み込む（起動時に 1 回）。</summary>
    public static void Load(UserSettings settings) => _placements = new Dictionary<string, SavedWindowPlacement>(settings.Windows);

    /// <summary>覚えた置き場所を、保存する設定へ入れる（閉じるとき）。</summary>
    public static void SaveTo(UserSettings settings) => settings.Windows = new Dictionary<string, SavedWindowPlacement>(_placements);

    /// <summary>覚えている置き場所（無ければ null）。</summary>
    public static SavedWindowPlacement? Get(string key) => _placements.GetValueOrDefault(key);

    /// <summary>置き場所を覚える（前の版の設定から移すときなど）。</summary>
    public static void Set(string key, SavedWindowPlacement placement) => _placements[key] = placement;

    /// <summary>前に控えた置き場所に戻す（null なら、覚えていないことに戻す）。確かめで窓の大きさを変えた回に、覚えを変えないため。</summary>
    public static void Restore(string key, SavedWindowPlacement? placement)
    {
        if (placement is null)
        {
            _placements.Remove(key);
        }
        else
        {
            _placements[key] = placement;
        }
    }

    /// <summary>
    /// 前の版の形式（WPF の座標。画面の左上が原点、拡大率 100% で 1 画素）を、置き場所の形式に直す。
    /// 拡大率はシステムの値で、原点は主モニターの作業領域の左上で直す。
    /// </summary>
    public static SavedWindowPlacement FromLegacy(WindowPlacement old)
    {
        double scale = Math.Max(GetDpiForSystem(), 96u) / 96.0;
        var origin = new NativeRect();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromPoint(new NativePoint(), MONITOR_DEFAULTTOPRIMARY), ref info))
        {
            origin = info.Work;
        }
        int left = (int)Math.Round(old.Left * scale) - origin.Left;
        int top = (int)Math.Round(old.Top * scale) - origin.Top;
        return new SavedWindowPlacement(left, top, left + (int)Math.Round(old.Width * scale), top + (int)Math.Round(old.Height * scale),
            old.IsMaximized);
    }

    /// <summary>
    /// ウィンドウを開く前（ShowDialog・Show の前）に呼ぶ。覚えた置き場所があれば開くときに戻し、閉じるときに覚える。
    /// 名前を省くと、ウィンドウの種類の名前で覚える。<paramref name="allowMaximize"/> なら最大化も戻す（メインのウィンドウ）。
    /// </summary>
    public static void Track(Window window, string? key = null, bool allowMaximize = false)
    {
        string name = key ?? window.GetType().Name;
        SavedWindowPlacement? saved = Get(name);
        if (saved is not null && IsReachable(saved))
        {
            // 初めの位置（親の真ん中など）へ動かされないようにする。実際の位置は、ウィンドウができた所で画素の座標で置く
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SourceInitialized += (_, _) => Restore(window, saved, allowMaximize);
        }
        window.Closing += (_, e) => Remember(window, name, e);
    }

    private static void Restore(Window window, SavedWindowPlacement saved, bool allowMaximize)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacementInfo { Length = Marshal.SizeOf<WindowPlacementInfo>() };
        if (hwnd == 0 || !GetWindowPlacement(hwnd, ref placement))
        {
            return;
        }
        // 大きさを変えられない画面（中身に合わせて高さが決まる画面）は、位置だけ戻す
        bool resizable = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
            && window.SizeToContent == SizeToContent.Manual;
        int width = resizable ? saved.Right - saved.Left : placement.NormalPosition.Right - placement.NormalPosition.Left;
        int height = resizable ? saved.Bottom - saved.Top : placement.NormalPosition.Bottom - placement.NormalPosition.Top;
        placement.NormalPosition = new NativeRect { Left = saved.Left, Top = saved.Top, Right = saved.Left + width, Bottom = saved.Top + height };
        placement.ShowCommand = SW_HIDE; // まだ見せない（見せるのは WPF に任せる）
        placement.Flags = 0;
        SetWindowPlacement(hwnd, ref placement);
        if (allowMaximize && saved.Maximized)
        {
            window.WindowState = WindowState.Maximized; // 元の位置のモニターで最大化する
        }
    }

    private static void Remember(Window window, string name, CancelEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }
        nint hwnd = new WindowInteropHelper(window).Handle;
        var placement = new WindowPlacementInfo { Length = Marshal.SizeOf<WindowPlacementInfo>() };
        if (hwnd == 0 || !GetWindowPlacement(hwnd, ref placement))
        {
            return;
        }
        NativeRect r = placement.NormalPosition;
        bool maximized = placement.ShowCommand == SW_SHOWMAXIMIZED
            || (placement.ShowCommand == SW_SHOWMINIMIZED && (placement.Flags & WPF_RESTORETOMAXIMIZED) != 0);
        _placements[name] = new SavedWindowPlacement(r.Left, r.Top, r.Right, r.Bottom, maximized);
    }

    /// <summary>
    /// 覚えた位置のタイトルバー（上の端から 32 画素の帯。左右は少し内側）が、どこかのモニターに出るか。
    /// 出ないとつかんで動かせないので戻さない。置き場所は作業領域の座標なので、主モニターの作業領域の左上の分だけずらして比べる。
    /// </summary>
    public static bool IsReachable(SavedWindowPlacement saved)
    {
        if (saved.Right - saved.Left < 100 || saved.Bottom - saved.Top < 60)
        {
            return false; // 壊れた値
        }
        var origin = new NativeRect();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromPoint(new NativePoint(), MONITOR_DEFAULTTOPRIMARY), ref info))
        {
            origin = info.Work;
        }
        var titleBar = new NativeRect
        {
            Left = saved.Left + origin.Left + 40,
            Top = saved.Top + origin.Top,
            Right = saved.Right + origin.Left - 40,
            Bottom = saved.Top + origin.Top + 32,
        };
        return MonitorFromRect(ref titleBar, MONITOR_DEFAULTTONULL) != 0;
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hwnd, ref WindowPlacementInfo placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint hwnd, ref WindowPlacementInfo placement);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref NativeRect rect, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
