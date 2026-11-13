using System.Collections.Concurrent;
using System.IO;
using Theater3D.App.Logging;
using Theater3D.Media;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの仮想カメラ。入れると、絵を渡すパイプの受け口を開き、Windows に仮想カメラ「3D Theater」を出す。
/// ほかのアプリ（Zoom・Discord・OBS など）がこのカメラを使っている間だけ、配信用の画面と同じ絵を描いて渡す（<see cref="VirtualCameraTarget"/>）。
/// 切る・アプリを閉じると、ほかのアプリのカメラの一覧から消える（入れている間だけ出す。利用者と相談して決めた）。
/// 仮想カメラを作る・しまうのは、COM の決まりに合わせて専用のスレッド（MTA）で行う（作るのに 1 秒ほどかかることもある）。
/// </summary>
public sealed class VirtualCameraService : IDisposable
{
    private readonly EditorContext _context;
    private readonly VirtualCameraFrames _frames = new();
    private readonly BlockingCollection<Action> _work = [];
    private readonly Thread _thread;
    private VirtualCameraServer? _server;
    private VirtualCameraTarget? _target;
    private VirtualCameraDevice? _device; // 専用のスレッドだけが触る
    private bool _disposed;

    internal VirtualCameraService(EditorContext context)
    {
        _context = context;
        _thread = new Thread(Run) { IsBackground = true, Name = "仮想カメラ" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        context.LiveOutput.Changed += (_, _) => UpdatePreferredSize();
        UpdatePreferredSize();
    }

    /// <summary>入れた・切った・つながった（画面のスレッドで来る）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>この Windows で使えるか（Windows 11 以降）。</summary>
    public static bool IsSupported => VirtualCameraDevice.IsSupported;

    /// <summary>入れているか（入れている途中も含む）。</summary>
    public bool IsOn { get; private set; }

    /// <summary>入れている途中・切っている途中か。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>このカメラを使っているアプリがあるか（カメラの元がつないでいるか）。</summary>
    public bool IsInUse => _server?.Connections > 0;

    /// <summary>描いて渡した絵の数（確かめ用）。</summary>
    public long FramesSent => _target?.FramesRendered ?? 0;

    /// <summary>今の様子（状態表示などに出す）。</summary>
    public string Status { get; private set; } = "仮想カメラは切っています";

    /// <summary>
    /// 入れる（先に <see cref="VirtualCameraSetup"/> で登録しておくこと）。うまくいかなければ例外（説明は Message）。
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOn || IsBusy)
        {
            return;
        }
        IsBusy = true;
        IsOn = true;
        SetStatus("仮想カメラを出しています…");
        try
        {
            UpdatePreferredSize();
            _server = new VirtualCameraServer(_frames);
            _server.ConnectionsChanged += OnConnectionsChanged;
            _server.Start(); // ほかの 3D Theater が使っていれば IOException
            _target = new VirtualCameraTarget(_context, _frames);
            await RunAsync(() => _device = VirtualCameraDevice.Create());
            AppLog.Info($"仮想カメラ「{VirtualCameraProtocol.FriendlyName}」を出しました（{_context.LiveOutput.Width} × {_context.LiveOutput.Height}）");
            SetStatus(StatusText());
        }
        catch (Exception ex) when (ex is IOException or VideoException or InvalidOperationException)
        {
            AppLog.Error("仮想カメラを出せませんでした", ex);
            await StopCoreAsync();
            SetStatus("仮想カメラを出せませんでした");
            throw;
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>切る（ほかのアプリの一覧から消える）。</summary>
    public async Task StopAsync()
    {
        if (!IsOn || IsBusy)
        {
            return;
        }
        IsBusy = true;
        SetStatus("仮想カメラをしまっています…");
        try
        {
            await StopCoreAsync();
            AppLog.Info("仮想カメラをしまいました");
            SetStatus("仮想カメラは切っています");
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>アプリを閉じるとき: しまってから、専用のスレッドを終える（待つのは長くても数秒）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (IsOn)
        {
            _target?.Dispose();
            _target = null;
            Task destroy = RunAsync(DestroyDevice);
            destroy.Wait(TimeSpan.FromSeconds(5));
            _server?.Dispose();
            _server = null;
            IsOn = false;
            AppLog.Info("アプリを閉じるため、仮想カメラをしまいました");
        }
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private async Task StopCoreAsync()
    {
        _target?.Dispose();
        _target = null;
        await RunAsync(DestroyDevice);
        if (_server is not null)
        {
            _server.ConnectionsChanged -= OnConnectionsChanged;
            _server.Dispose();
            _server = null;
        }
        IsOn = false;
    }

    private void DestroyDevice()
    {
        _device?.Dispose();
        _device = null;
    }

    private void UpdatePreferredSize()
    {
        int width = _context.LiveOutput.Width & ~1;
        int height = _context.LiveOutput.Height & ~1;
        if (VirtualCameraProtocol.IsValidSize(width, height))
        {
            _frames.PreferredSize = (width, height);
        }
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (IsOn && !IsBusy)
            {
                SetStatus(StatusText());
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        });

    private string StatusText() => IsInUse
        ? "仮想カメラ: ほかのアプリが使っています"
        : $"仮想カメラ: 出しています（ほかのアプリのカメラで「{VirtualCameraProtocol.FriendlyName}」を選べます）";

    private void SetStatus(string status)
    {
        if (Status != status)
        {
            Status = status;
            AppLog.Info(status);
        }
    }

    // 専用のスレッドで行い、終わるのを待つ（画面のスレッドは止めない）
    private Task RunAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            done.SetException(ex); // もう閉じている
        }
        return done.Task;
    }

    private void Run()
    {
        foreach (Action action in _work.GetConsumingEnumerable())
        {
            action();
        }
    }
}
