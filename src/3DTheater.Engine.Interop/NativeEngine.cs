namespace Theater3D.Engine.Interop;

[Flags]
public enum NativeEngineOptions : uint
{
    None = 0,
    /// <summary>Direct3D の検査機能を使う。入っていない環境では警告を出して無しで動く。</summary>
    DebugLayer = 1u << 0,
    /// <summary>GPU を使わないソフトウェア描画（テスト・診断用）。</summary>
    UseWarp = 1u << 1,
}

public sealed record EngineInfo(
    string AdapterName,
    string FeatureLevel,
    bool IsSoftware,
    bool DebugLayerEnabled,
    ulong DedicatedVideoMemory);

public readonly record struct VideoMemoryInfo(ulong Usage, ulong Budget);

/// <summary>
/// ネイティブエンジン（GPU デバイスと共有資源）。作ったスレッドからだけ使い、
/// 作ったビューをすべて破棄してから <see cref="Dispose"/> すること。
/// </summary>
public sealed unsafe class NativeEngine : IDisposable
{
    public const int ExpectedApiVersionMajor = 0;
    public const int ExpectedApiVersionMinor = 3;

    private nint _handle;

    private NativeEngine(nint handle)
    {
        _handle = handle;
        Info = ReadInfo();
    }

    public EngineInfo Info { get; }

    public bool IsDisposed => _handle == 0;

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    /// <summary>読み込まれた DLL の版。DLL が見つからないときは <see cref="DllNotFoundException"/>。</summary>
    public static (int Major, int Minor) ApiVersion
    {
        get
        {
            uint version = NativeMethods.GetApiVersion();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }
    }

    public static NativeEngine Create(NativeEngineOptions options = NativeEngineOptions.None)
    {
        var (major, minor) = ApiVersion;
        if (major != ExpectedApiVersionMajor || minor < ExpectedApiVersionMinor)
        {
            throw new NativeEngineException(
                NativeResult.NotSupported,
                $"ネイティブエンジンの版が合いません（DLL: {major}.{minor}、必要: {ExpectedApiVersionMajor}.{ExpectedApiVersionMinor} 以上）");
        }

        NativeLog.EnsureRegistered();
        var desc = new TtEngineDesc { StructSize = (uint)sizeof(TtEngineDesc), Flags = (uint)options };
        nint handle;
        NativeError.Check(NativeMethods.EngineCreate(&desc, &handle));
        return new NativeEngine(handle);
    }

    public VideoMemoryInfo QueryVideoMemory()
    {
        var info = new TtVideoMemoryInfo { StructSize = (uint)sizeof(TtVideoMemoryInfo) };
        NativeError.Check(NativeMethods.EngineQueryVideoMemory(Handle, &info));
        return new VideoMemoryInfo(info.Usage, info.Budget);
    }

    /// <summary>テクスチャを作る。画素は BGRA 8 ビット（透明度をかけ合わせていない値）、上の行から。</summary>
    public NativeTexture CreateTexture(int width, int height, ReadOnlySpan<byte> bgraPixels, int stride)
    {
        if (stride < width * 4 || (long)stride * height > bgraPixels.Length)
        {
            throw new ArgumentException("テクスチャの画素の量が大きさに足りません", nameof(bgraPixels));
        }
        nint texture;
        fixed (byte* pixels = bgraPixels)
        {
            NativeError.Check(NativeMethods.TextureCreate(Handle, width, height, pixels, stride, &texture));
        }
        return new NativeTexture(texture);
    }

    /// <summary>モデルの形を GPU に載せる。面の頂点番号と材質の範囲が正しくなければ <see cref="NativeEngineException"/>。</summary>
    public NativeMesh CreateMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, IReadOnlyList<MeshMaterial> materials)
    {
        var nativeMaterials = new TtMeshMaterial[materials.Count];
        for (int i = 0; i < materials.Count; i++)
        {
            MeshMaterial m = materials[i];
            nativeMaterials[i] = new TtMeshMaterial
            {
                IndexStart = m.IndexStart,
                IndexCount = m.IndexCount,
                Diffuse = m.Diffuse,
                Specular = m.Specular,
                SpecularPower = m.SpecularPower,
                Ambient = m.Ambient,
                Flags = (uint)m.Flags,
                Texture = m.Texture?.Handle ?? 0,
            };
        }

        nint mesh;
        fixed (MeshVertex* vertexPointer = vertices)
        fixed (uint* indexPointer = indices)
        fixed (TtMeshMaterial* materialPointer = nativeMaterials)
        {
            var desc = new TtMeshDesc
            {
                StructSize = (uint)sizeof(TtMeshDesc),
                VertexCount = (uint)vertices.Length,
                Vertices = vertexPointer,
                Indices = indexPointer,
                IndexCount = (uint)indices.Length,
                MaterialCount = (uint)nativeMaterials.Length,
                Materials = materialPointer,
            };
            NativeError.Check(NativeMethods.MeshCreate(Handle, &desc, &mesh));
        }
        return new NativeMesh(mesh);
    }

    /// <summary>それまでに頼んだ描画を GPU が終えるまで待つ。共有ビューを全部描いてから 1 回だけ呼ぶ。</summary>
    public void WaitForGpu() => NativeError.Check(NativeMethods.EngineWaitForGpu(Handle));

    /// <summary>親ウィンドウの子ウィンドウに直接描くビュー（WPF の HwndHost 用）。</summary>
    public NativeView CreateWindowView(nint parentHwnd, int width, int height)
    {
        nint view;
        NativeError.Check(NativeMethods.ViewCreateForWindow(Handle, parentHwnd, width, height, &view));
        return new NativeView(this, view);
    }

    /// <summary>画面外に描くビュー。<paramref name="shareWithD3D9"/> を true にすると WPF の D3DImage に渡せる。</summary>
    public NativeView CreateOffscreenView(int width, int height, bool shareWithD3D9 = false)
    {
        nint view;
        uint flags = shareWithD3D9 ? 1u : 0u;
        NativeError.Check(NativeMethods.ViewCreateOffscreen(Handle, width, height, flags, &view));
        return new NativeView(this, view);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        NativeError.Check(NativeMethods.EngineDestroy(_handle));
        _handle = 0;
    }

    private EngineInfo ReadInfo()
    {
        var info = new TtEngineInfo { StructSize = (uint)sizeof(TtEngineInfo) };
        NativeError.Check(NativeMethods.EngineGetInfo(_handle, &info));
        string adapterName = new(info.AdapterName);
        string featureLevel = $"{(info.FeatureLevel >> 12) & 0xF}_{(info.FeatureLevel >> 8) & 0xF}";
        return new EngineInfo(adapterName, featureLevel, info.IsSoftware != 0, info.DebugLayerEnabled != 0, info.DedicatedVideoMemory);
    }
}
