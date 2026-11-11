namespace Theater3D.Engine.Interop;

/// <summary>
/// GPU に載せた環境の画像（全方向の景色を、横 360 度・縦 180 度に広げた HDR 画像）。PBR の材質の環境光と映り込み、背景に使う。
/// 描く間は破棄しないこと（<see cref="FrameParameters.Environment"/> で渡す）。
/// </summary>
public sealed class NativeEnvironment : IDisposable
{
    private nint _handle;

    internal NativeEnvironment(nint handle, int width, int height)
    {
        _handle = handle;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsDisposed => _handle == 0;

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        NativeError.Check(NativeMethods.EnvironmentDestroy(_handle));
        _handle = 0;
    }
}
