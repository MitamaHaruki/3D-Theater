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

/// <summary>GPU に載せたモデルの形。エンジンと同じスレッドで使い、エンジンより先に破棄する。</summary>
public sealed class NativeMesh : IDisposable
{
    private nint _handle;

    internal NativeMesh(nint handle) => _handle = handle;

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
