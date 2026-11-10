using System.Numerics;
using System.Runtime.InteropServices;

namespace Theater3D.Engine.Interop;

/// <summary>メッシュの頂点（tt_engine.h の TtMeshVertex と同じ並び）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex(Vector3 position, Vector3 normal, Vector2 uv)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public Vector2 Uv = uv;
}

[Flags]
public enum MaterialFlags : uint
{
    None = 0,
    /// <summary>裏面も描く。</summary>
    DoubleSided = 1u << 0,

    /// <summary>輪郭線を描く。</summary>
    Edge = 1u << 1,
}

/// <summary>スフィアマップの使い方（tt_engine.h の TT_SPHERE_*）。</summary>
public enum SphereMode : uint
{
    None = 0,

    /// <summary>色に掛ける（.sph）。</summary>
    Multiply = 1,

    /// <summary>色に足す（.spa）。</summary>
    Add = 2,
}

/// <summary>
/// メッシュの材質。面の頂点番号の <see cref="IndexStart"/> から <see cref="IndexCount"/> 個をこの材質で描く。
/// 材質は並び順に描く（MMD と同じ）。色の計算は MMD と同じ: (拡散色 × 照明の色 + 環境色) × テクスチャ → スフィア → × トゥーン → + 反射光。
/// <see cref="ToonTexture"/> が無ければ陰影を付けない。輪郭線は <see cref="MaterialFlags.Edge"/> のときに、太さ <see cref="EdgeSize"/> で描く。
/// </summary>
public readonly record struct MeshMaterial(
    uint IndexStart,
    uint IndexCount,
    Vector4 Diffuse,
    Vector3 Specular,
    float SpecularPower,
    Vector3 Ambient,
    MaterialFlags Flags,
    NativeTexture? Texture,
    NativeTexture? ToonTexture = null,
    NativeTexture? SphereTexture = null,
    SphereMode SphereMode = SphereMode.None,
    float EdgeSize = 0,
    Vector4 EdgeColor = default);

/// <summary>GPU に載せたテクスチャ。メッシュを作った後は破棄してよい（メッシュが中身を参照し続ける）。</summary>
public sealed class NativeTexture : IDisposable
{
    private nint _handle;

    internal NativeTexture(nint handle) => _handle = handle;

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
        NativeError.Check(NativeMethods.TextureDestroy(_handle));
        _handle = 0;
    }
}

/// <summary>
/// 材質の今の値（tt_engine.h の TtMaterialState と同じ並び）。テクスチャ・スフィア・トゥーンの色は「元の色 × Multiply + Add」。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MaterialState
{
    public Vector4 Diffuse;
    public Vector3 Specular;
    public float SpecularPower;
    public Vector3 Ambient;
    private uint _reserved;
    public Vector4 TextureMultiply;
    public Vector4 TextureAdd;
    public Vector4 SphereMultiply;
    public Vector4 SphereAdd;
    public Vector4 ToonMultiply;
    public Vector4 ToonAdd;
    public Vector4 EdgeColor;
    public float EdgeSize;
    private uint _reserved2;
    private uint _reserved3;
    private uint _reserved4;
}

/// <summary>重ね描きの線の頂点（2 つで 1 本、座標はワールド）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LineVertex(Vector3 position, Vector4 color)
{
    public Vector3 Position = position;
    public Vector4 Color = color;
}

/// <summary>GPU に載せたモデルの形。エンジンと同じスレッドで使い、エンジンより先に破棄する。</summary>
public sealed unsafe class NativeMesh : IDisposable
{
    private nint _handle;

    internal NativeMesh(nint handle) => _handle = handle;

    public bool IsDisposed => _handle == 0;

    /// <summary>変形した後の頂点で置き換える（数は作ったときと同じ）。囲む箱は選択の強調に使う。</summary>
    public void UpdateVertices(ReadOnlySpan<MeshVertex> vertices, Vector3 boundsMin, Vector3 boundsMax)
    {
        fixed (MeshVertex* pointer = vertices)
        {
            NativeError.Check(NativeMethods.MeshUpdateVertices(Handle, pointer, (uint)vertices.Length, &boundsMin, &boundsMax));
        }
    }

    /// <summary>材質の今の値を置き換える（数は作ったときと同じ）。</summary>
    public void UpdateMaterials(ReadOnlySpan<MaterialState> states)
    {
        fixed (MaterialState* pointer = states)
        {
            NativeError.Check(NativeMethods.MeshUpdateMaterials(Handle, pointer, (uint)states.Length));
        }
    }

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
        NativeError.Check(NativeMethods.MeshDestroy(_handle));
        _handle = 0;
    }
}

[Flags]
public enum DrawFlags : uint
{
    None = 0,
    /// <summary>選択中。外接する箱を強調色で描く。</summary>
    Selected = 1u << 0,
}

/// <summary>1 枚の描画で描くモデル 1 つ。</summary>
public readonly record struct DrawItem(NativeMesh Mesh, Matrix4x4 World, DrawFlags Flags);
