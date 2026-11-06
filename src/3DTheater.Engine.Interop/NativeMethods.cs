using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Theater3D.Engine.Interop;

// tt_engine.h と 1 対 1 で対応させる。構造体の並びと大きさを変えたら C++ 側も必ず合わせる。

[StructLayout(LayoutKind.Sequential)]
internal struct TtEngineDesc
{
    public uint StructSize;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TtEngineInfo
{
    public uint StructSize;
    public uint FeatureLevel;
    public uint IsSoftware;
    public uint DebugLayerEnabled;
    public ulong DedicatedVideoMemory;
    public fixed char AdapterName[128];
}

[StructLayout(LayoutKind.Sequential)]
internal struct TtVideoMemoryInfo
{
    public uint StructSize;
    public uint Reserved;
    public ulong Usage;
    public ulong Budget;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TtFrameParams
{
    public uint StructSize;
    public uint Flags;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Matrix4x4 GridWorld;
    public Matrix4x4 TestObjectWorld;
    public TtDrawItem* Items;
    public uint ItemCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TtDrawItem
{
    public nint Mesh;
    public Matrix4x4 World;
    public uint Flags;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TtMeshMaterial
{
    public uint IndexStart;
    public uint IndexCount;
    public Vector4 Diffuse;
    public Vector3 Specular;
    public float SpecularPower;
    public Vector3 Ambient;
    public uint Flags;
    public nint Texture;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TtMeshDesc
{
    public uint StructSize;
    public uint VertexCount;
    public MeshVertex* Vertices;
    public uint* Indices;
    public uint IndexCount;
    public uint MaterialCount;
    public TtMeshMaterial* Materials;
}

internal static unsafe partial class NativeMethods
{
    private const string Library = "3DTheater.Engine.Native";

    [LibraryImport(Library, EntryPoint = "tt_get_api_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetApiVersion();

    [LibraryImport(Library, EntryPoint = "tt_set_log_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetLogCallback(delegate* unmanaged[Cdecl]<int, char*, nint, void> callback, nint userData);

    [LibraryImport(Library, EntryPoint = "tt_get_last_error_message")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetLastErrorMessage(char* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "tt_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EngineCreate(TtEngineDesc* desc, nint* outEngine);

    [LibraryImport(Library, EntryPoint = "tt_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EngineDestroy(nint engine);

    [LibraryImport(Library, EntryPoint = "tt_engine_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EngineGetInfo(nint engine, TtEngineInfo* outInfo);

    [LibraryImport(Library, EntryPoint = "tt_engine_query_video_memory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EngineQueryVideoMemory(nint engine, TtVideoMemoryInfo* outInfo);

    [LibraryImport(Library, EntryPoint = "tt_texture_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int TextureCreate(nint engine, int width, int height, void* pixels, int stride, nint* outTexture);

    [LibraryImport(Library, EntryPoint = "tt_texture_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int TextureDestroy(nint texture);

    [LibraryImport(Library, EntryPoint = "tt_mesh_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int MeshCreate(nint engine, TtMeshDesc* desc, nint* outMesh);

    [LibraryImport(Library, EntryPoint = "tt_mesh_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int MeshDestroy(nint mesh);

    [LibraryImport(Library, EntryPoint = "tt_engine_wait_for_gpu")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EngineWaitForGpu(nint engine);

    [LibraryImport(Library, EntryPoint = "tt_view_create_for_window")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewCreateForWindow(nint engine, nint parentHwnd, int width, int height, nint* outView);

    [LibraryImport(Library, EntryPoint = "tt_view_create_offscreen")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewCreateOffscreen(nint engine, int width, int height, uint flags, nint* outView);

    [LibraryImport(Library, EntryPoint = "tt_view_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewDestroy(nint view);

    [LibraryImport(Library, EntryPoint = "tt_view_get_window")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewGetWindow(nint view, nint* outHwnd);

    [LibraryImport(Library, EntryPoint = "tt_view_get_d3d9_surface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewGetD3D9Surface(nint view, nint* outSurface);

    [LibraryImport(Library, EntryPoint = "tt_view_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewResize(nint view, int width, int height);

    [LibraryImport(Library, EntryPoint = "tt_view_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewRender(nint view, TtFrameParams* parameters);

    [LibraryImport(Library, EntryPoint = "tt_view_read_pixels")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ViewReadPixels(nint view, void* buffer, int stride, int bufferSize);
}
