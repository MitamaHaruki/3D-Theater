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
    /// なめらかさ（MSAA。1 画素あたりの点の数 1・2・4・8）を選び、実際に使う数を返す（GPU が使えない数なら少なくする）。
    /// 中の描画先を作り直すが、<see cref="D3D9Surface"/> は変わらない。
    /// </summary>
    public int SetSampleCount(int sampleCount)
    {
        uint actual;
        NativeError.Check(NativeMethods.ViewSetSampleCount(Handle, (uint)sampleCount, &actual));
        return (int)actual;
    }

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
        // 足す照明（エンジンが使うのは最大 8 つ）
        int lightCount = Math.Min(frame.Lights?.Count ?? 0, LightSource.MaxCount);
        Span<TtLight> lights = stackalloc TtLight[lightCount];
        for (int i = 0; i < lightCount; i++)
        {
            LightSource light = frame.Lights![i];
            lights[i] = new TtLight
            {
                Type = (uint)light.Kind,
                Position = light.Position,
                Direction = light.Direction,
                Range = light.Range,
                Color = light.Color,
                SpotAngle = light.SpotAngle,
                SpotSoftness = light.SpotSoftness,
            };
        }
        fixed (TtDrawItem* itemPointer = items)
        fixed (LineVertex* linePointer = lines)
        fixed (TtLight* lightPointer = lights)
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
                PostFlags = (uint)frame.Effects.Flags,
                Exposure = frame.Effects.Exposure,
                Contrast = frame.Effects.Contrast,
                Saturation = frame.Effects.Saturation,
                Temperature = frame.Effects.Temperature,
                BloomIntensity = frame.Effects.BloomIntensity,
                BloomThreshold = frame.Effects.BloomThreshold,
                BloomSpread = frame.Effects.BloomSpread,
                Vignette = frame.Effects.Vignette,
                LineColor = frame.Effects.LineColor,
                LineWidth = frame.Effects.LineWidth,
                LineStrength = frame.Effects.LineStrength,
                LineDetail = frame.Effects.LineDetail,
                PosterizeLevels = (uint)Math.Clamp(frame.Effects.PosterizeLevels, 2, 16),
                ShadowTintStrength = frame.Effects.ShadowTintStrength,
                ShadowTint = frame.Effects.ShadowTint,
                PaperStrength = frame.Effects.PaperStrength,
                PaperGrain = frame.Effects.PaperGrain,
                Lights = lightCount > 0 ? lightPointer : null,
                LightCount = (uint)lightCount,
                ReflectionStrength = frame.ReflectionStrength,
                ReflectionFade = frame.ReflectionFade,
                ReflectionBlur = frame.ReflectionBlur,
                RaySamples = (uint)Math.Clamp(frame.RaySamples, 0, 64),
                DofFocusDistance = frame.Effects.DofFocusDistance,
                DofFocusRange = frame.Effects.DofFocusRange,
                DofMaxBlur = frame.Effects.DofMaxBlur,
                DofTransition = frame.Effects.DofTransition,
                AoStrength = frame.Effects.AoStrength,
                AoRadius = frame.Effects.AoRadius,
                FogColor = frame.Effects.FogColor,
                FogStart = frame.Effects.FogStart,
                FogDensity = frame.Effects.FogDensity,
                FogHeight = frame.Effects.FogHeight,
                ShadowSoftness = frame.ShadowSoftness,
                Environment = frame.Environment?.Handle ?? 0,
                EnvironmentIntensity = frame.EnvironmentIntensity,
                EnvironmentRotation = frame.EnvironmentRotation,
                RenderPass = (uint)frame.Pass,
                PassDepthNear = frame.PassDepthNear,
                PassDepthFar = frame.PassDepthFar,
                SsrStrength = frame.Effects.SsrStrength,
                SsrMaxDistance = frame.Effects.SsrMaxDistance,
            };
            return NativeError.Check(NativeMethods.ViewRender(Handle, &parameters)) == NativeResult.Ok;
        }
    }

    /// <summary>
    /// 見える面を分けたビュー（<see cref="NativeEngine.CreateOffscreenView"/> の deferredPublish）で、最後に描いた絵を見える面
    /// （<see cref="D3D9Surface"/> と <see cref="ReadPixels"/> で読む面）へ写すよう GPU に頼む。完了は待たないので、
    /// WPF へ渡す前に <see cref="NativeEngine.WaitForGpu"/> を呼ぶこと。大きさを変えた後にまだ描いていなければ何もしない。
    /// 見える面を分けていないビューでは何もしない。
    /// </summary>
    public void Publish() => NativeError.Check(NativeMethods.ViewPublish(Handle));

    /// <summary>
    /// 1 回の描画（<see cref="Render"/> の中で GPU に頼んだ分）に GPU が掛けた時間（ミリ秒）。待たずに読むので、数回前の描画の値。
    /// まだ測れていなければ null。
    /// </summary>
    public float? GpuMilliseconds
    {
        get
        {
            float milliseconds;
            return NativeError.Check(NativeMethods.ViewGetGpuTime(Handle, &milliseconds)) == NativeResult.Ok ? milliseconds : null;
        }
    }

    /// <summary>
    /// 画面外ビューの内容を BGRA 8 ビットで読み出す。テスト・保存用と、共有できない環境での表示用。
    /// 見える面を分けたビューでは、最後に <see cref="Publish"/> した絵を読む。
    /// </summary>
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
