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
}

/// <summary>
/// メッシュの材質。面の頂点番号の <see cref="IndexStart"/> から <see cref="IndexCount"/> 個をこの材質で描く。
/// 材質は並び順に描く（MMD と同じ）。
/// </summary>
public readonly record struct MeshMaterial(
    uint IndexStart,
    uint IndexCount,
    Vector4 Diffuse,
    Vector3 Specular,
    float SpecularPower,
    Vector3 Ambient,
    MaterialFlags Flags,
    NativeTexture? Texture);

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

/// <summary>材質の今の値（tt_engine.h の TtMaterialState と同じ並び）。テクスチャの色は「テクスチャ × TextureMultiply + TextureAdd」。</summary>
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
