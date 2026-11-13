using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// Windows のホットキー（RegisterHotKey）。ほかのアプリを使っている間も効き、そのキーは使っているアプリには渡らない
/// （利用者と相談して決めた）。押したら <see cref="Pressed"/>。押している間だけのキーは、<see cref="WatchRelease"/> で見張り、
/// 離したら <see cref="Released"/>（RegisterHotKey は離したことを知らせないので、キーの今の状態を短い間隔で確かめる）。
/// 知らせは、メッセージだけを受ける見えない窓で受ける（画面のスレッド）。
/// </summary>
internal sealed partial class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000; // 押し続けても、繰り返し知らせない
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _window;
    private readonly Dictionary<int, HotkeyGesture> _registered = [];
    private readonly Dictionary<int, int> _held = []; // 番号 → 見張るキー
    private readonly DispatcherTimer _releaseTimer;
    private bool _disposed;

    public GlobalHotkeys()
    {
        var parameters = new HwndSourceParameters("3D Theater キー操作") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = MessageOnlyParent };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
        _releaseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => CheckReleased(), Dispatcher.CurrentDispatcher);
    }

    /// <summary>キーが押された（番号）。</summary>
    public event EventHandler<int>? Pressed;

    /// <summary>見張っているキーが離された（番号）。</summary>
    public event EventHandler<int>? Released;

    /// <summary>登録する。ほかのアプリが同じキーを使っていれば false。</summary>
    public bool Register(int id, HotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister(id);
        if (!RegisterHotKey(_window.Handle, id, (uint)gesture.Modifiers | ModNoRepeat, (uint)gesture.Key))
        {
            return false;
        }
        _registered[id] = gesture;
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            UnregisterHotKey(_window.Handle, id);
        }
        _held.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.Keys.ToArray())
        {
            Unregister(id);
        }
    }

    /// <summary>押されたキーが離されるのを見張る（離されたら <see cref="Released"/>）。</summary>
    public void WatchRelease(int id)
    {
        if (_registered.TryGetValue(id, out HotkeyGesture gesture))
        {
            _held[id] = gesture.Key;
            _releaseTimer.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _releaseTimer.Stop();
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private void CheckReleased()
    {
        foreach (var (id, key) in _held.ToArray())
        {
            if ((GetAsyncKeyState(key) & 0x8000) == 0)
            {
                _held.Remove(id);
                Released?.Invoke(this, id);
            }
        }
        if (_held.Count == 0)
        {
            _releaseTimer.Stop();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.ContainsKey(wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke(this, wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}
