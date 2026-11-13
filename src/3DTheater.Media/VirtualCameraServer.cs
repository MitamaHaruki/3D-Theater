using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Theater3D.Media;

/// <summary>
/// 仮想カメラの元（DLL）へ絵を渡す、名前付きパイプの受け口（<see cref="VirtualCameraProtocol"/> の決まり）。
/// DLL は Windows のカメラの仕組み（Frame Server。Local Service の権限・別のセッション）の中で動くので、
/// パイプには、この利用者に加えて Local Service・Local System・管理者だけがつなげるようにする。
/// つなぐ口は 4 つまで。口ごとに、頼まれたら <see cref="VirtualCameraFrames"/> から NV12 を取って返す。
/// 待ちはすべて非同期で、止めるとき（<see cref="Dispose"/>）にほどく（同期の接続待ちは、パイプを閉じてもほどけないため）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VirtualCameraServer : IDisposable
{
    /// <summary>新しい絵を待つ長さ（無ければ、前の絵か「まだ無い」を返す）。</summary>
    public static readonly TimeSpan FrameWait = TimeSpan.FromMilliseconds(200);

    private const int MaxInstances = 4;
    private readonly VirtualCameraFrames _frames;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _listeners = [];
    private bool _disposed;
    private int _connected;

    public VirtualCameraServer(VirtualCameraFrames frames, string pipeName = VirtualCameraProtocol.PipeName)
    {
        _frames = frames;
        _pipeName = pipeName;
    }

    /// <summary>つながっている DLL の数が変わった（どのスレッドからも来る）。</summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>今つながっている DLL の数。</summary>
    public int Connections => Volatile.Read(ref _connected);

    /// <summary>
    /// 受け付けを始める。同じ名前のパイプがもうあれば（ほかの 3D Theater が仮想カメラを使っている）、<see cref="IOException"/>。
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listeners.Count > 0)
        {
            return;
        }
        // 最初の口は「初めてのパイプ」として作り、ほかのプロセスが同じ名前で作っていないことを確かめる
        NamedPipeServerStream first;
        try
        {
            first = CreatePipe(firstInstance: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("仮想カメラのパイプは、ほかのアプリ（3D Theater）が使っています", ex);
        }
        CancellationToken token = _stop.Token;
        for (int i = 0; i < MaxInstances; i++)
        {
            NamedPipeServerStream? initial = i == 0 ? first : null;
            _listeners.Add(Task.Run(() => ListenAsync(initial, token)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop.Cancel();
        try
        {
            Task.WaitAll([.. _listeners], TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var security = new PipeSecurity();
        const PipeAccessRights ReadWrite = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        if (WindowsIdentity.GetCurrent().User is { } user)
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        foreach (WellKnownSidType sid in (WellKnownSidType[])[WellKnownSidType.LocalServiceSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid])
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), ReadWrite, AccessControlType.Allow));
        }
        PipeOptions options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, options,
            inBufferSize: 4096, outBufferSize: 1 << 20, security);
    }

    private async Task ListenAsync(NamedPipeServerStream? initial, CancellationToken token)
    {
        NamedPipeServerStream? pipe = initial;
        while (!token.IsCancellationRequested)
        {
            try
            {
                pipe ??= CreatePipe(firstInstance: false);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref _connected);
                ConnectionsChanged?.Invoke(this, EventArgs.Empty);
                try
                {
                    await ServeAsync(pipe, token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _connected);
                    ConnectionsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
            {
                // 切れた・閉じた。止めていなければ、新しい口を作って待ち直す
                if (!token.IsCancellationRequested)
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                pipe?.Dispose();
                pipe = null;
            }
        }
        pipe?.Dispose();
    }

    // つながっている間、頼み → 返事 を繰り返す。決まりと違う頼みが来たら切る
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        byte[] requestBuffer = new byte[VirtualCameraProtocol.RequestSize];
        byte[] headerBuffer = new byte[VirtualCameraProtocol.HeaderSize];
        byte[] payload = [];
        while (!token.IsCancellationRequested && pipe.IsConnected)
        {
            await pipe.ReadExactlyAsync(requestBuffer, token).ConfigureAwait(false);
            if (VirtualCameraProtocol.Request.Read(requestBuffer) is not { } request)
            {
                return;
            }
            if (request.IsSizeQuery)
            {
                var (width, height) = _frames.PreferredSize;
                new VirtualCameraProtocol.FrameHeader(width, height, VirtualCameraProtocol.FlagNoFrame, 0, 0).Write(headerBuffer);
                await pipe.WriteAsync(headerBuffer, token).ConfigureAwait(false);
                continue;
            }
            if (!VirtualCameraProtocol.IsValidSize(request.Width, request.Height))
            {
                return;
            }
            int bytes = VirtualCameraProtocol.Nv12Bytes(request.Width, request.Height);
            if (payload.Length != bytes)
            {
                payload = new byte[bytes];
            }
            // 新しい絵を少し待つ（スレッドプールのスレッドで待つ。つなぐ口は多くないので構わない）
            ulong sequence = _frames.CopyNv12(request.Width, request.Height, request.LastSequence, FrameWait, payload);
            var header = sequence == 0
                ? new VirtualCameraProtocol.FrameHeader(request.Width, request.Height, VirtualCameraProtocol.FlagNoFrame, 0, 0)
                : new VirtualCameraProtocol.FrameHeader(request.Width, request.Height, 0, bytes, sequence);
            header.Write(headerBuffer);
            await pipe.WriteAsync(headerBuffer, token).ConfigureAwait(false);
            if (sequence != 0)
            {
                await pipe.WriteAsync(payload.AsMemory(0, bytes), token).ConfigureAwait(false);
            }
        }
    }
}
