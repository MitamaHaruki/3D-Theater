namespace Theater3D.Engine.Interop;

/// <summary>
/// 1 つの描画先。エンジンと同じスレッドから使い、エンジンより先に <see cref="Dispose"/> すること。
/// GPU とウィンドウの資源は作ったスレッドでしか安全に解放できないため、ファイナライザーでは解放しない。
/// </summary>
public sealed unsafe class NativeView : IDisposable
{
    private nint _handle;

    internal NativeView(NativeEngine engine, nint handle)
    {
        Engine = engine;
        _handle = handle;
    }

    public NativeEngine Engine { get; }

    public bool IsDisposed => _handle == 0;

    private nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    /// <summary>ウィンドウ用ビューの子ウィンドウ。画面外ビューでは 0。</summary>
    public nint WindowHandle
    {
        get
        {
            nint hwnd;
            NativeError.Check(NativeMethods.ViewGetWindow(Handle, &hwnd));
            return hwnd;
        }
    }

    /// <summary>D3DImage に渡す Direct3D 9 の面。<see cref="Resize"/> の後は別の値になる。</summary>
    public nint D3D9Surface
    {
        get
        {
            nint surface;
            NativeError.Check(NativeMethods.ViewGetD3D9Surface(Handle, &surface));
            return surface;
        }
    }

    /// <summary>大きさ（実ピクセル）を変える。幅か高さが 0 なら描画を止める。</summary>
    public void Resize(int width, int height) => NativeError.Check(NativeMethods.ViewResize(Handle, width, height));

    /// <summary>
    /// 1 枚描く。ウィンドウ用ビューで前の表示がまだ終わっていないときは、待たずに false を返す
    /// （UI スレッドを止めないため。次のフレームで呼び直す）。
    /// 画面外ビューは描画を頼むだけなので、WPF へ渡す前に <see cref="NativeEngine.WaitForGpu"/> を呼ぶこと。
    /// </summary>
    public bool Render(in FrameParameters frame)
    {
        int count = frame.Items?.Count ?? 0;
        // モデルの数は多くないので、ふつうはスタック上に並べる（毎フレームのごみを出さない）
        Span<TtDrawItem> items = count <= 64 ? stackalloc TtDrawItem[count] : new TtDrawItem[count];
        for (int i = 0; i < count; i++)
        {
            DrawItem item = frame.Items![i];
            items[i] = new TtDrawItem { Mesh = item.Mesh.Handle, World = item.World, Flags = (uint)item.Flags };
        }

        ReadOnlySpan<LineVertex> lines = frame.OverlayLines.Span;
        fixed (TtDrawItem* itemPointer = items)
        fixed (LineVertex* linePointer = lines)
        {
            var parameters = new TtFrameParams
            {
                StructSize = (uint)sizeof(TtFrameParams),
                Flags = (uint)frame.Flags,
                View = frame.View,
                Projection = frame.Projection,
                GridWorld = frame.GridWorld,
                TestObjectWorld = frame.TestObjectWorld,
                Items = count > 0 ? itemPointer : null,
                ItemCount = (uint)count,
                OverlayLines = lines.Length > 0 ? linePointer : null,
                OverlayVertexCount = (uint)(lines.Length - (lines.Length % 2)),
                LightDirection = frame.LightDirection,
                LightColor = frame.LightColor,
                EdgePixelScale = frame.EdgePixelScale,
                GroundShadowColor = frame.GroundShadowColor,
                ShadowMapSize = (uint)Math.Max(0, frame.ShadowMapSize),
                ClearColor = frame.ClearColor,
            };
            return NativeError.Check(NativeMethods.ViewRender(Handle, &parameters)) == NativeResult.Ok;
        }
    }

    /// <summary>画面外ビューの内容を BGRA 8 ビットで読み出す。テスト・保存用と、共有できない環境での表示用。</summary>
    public void ReadPixels(Span<byte> destination, int stride)
    {
        fixed (byte* buffer = destination)
        {
            NativeError.Check(NativeMethods.ViewReadPixels(Handle, buffer, stride, destination.Length));
        }
    }

    /// <summary>WriteableBitmap の BackBuffer など、確保済みの領域へ直接読み出す。</summary>
    public void ReadPixels(nint destination, int stride, int bufferSize) =>
        NativeError.Check(NativeMethods.ViewReadPixels(Handle, (void*)destination, stride, bufferSize));

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        NativeError.Check(NativeMethods.ViewDestroy(_handle));
        _handle = 0;
    }
}
