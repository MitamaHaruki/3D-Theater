using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Theater3D.Physics;

/// <summary>剛体の置き方（tp_physics.h の TpTransform と同じ並び）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsTransform
{
    public Vector3 Position;
    public Quaternion Rotation;

    public PhysicsTransform(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }

    /// <summary>行列（拡大を含まないこと）から作る。</summary>
    public static PhysicsTransform FromMatrix(Matrix4x4 m) =>
        new(m.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m)));

    /// <summary>2 つの置き方の間（<paramref name="t"/> = 0 で <paramref name="a"/>、1 で <paramref name="b"/>。位置は直線、向きは球面で補間）。</summary>
    public static PhysicsTransform Interpolate(in PhysicsTransform a, in PhysicsTransform b, float t) =>
        new(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)));

    public readonly Matrix4x4 ToMatrix()
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Rotation);
        m.Translation = Position;
        return m;
    }
}

public enum PhysicsShape : uint
{
    /// <summary>球。大きさの X が半径。</summary>
    Sphere = 0,

    /// <summary>箱。大きさは各軸の半分の長さ。</summary>
    Box = 1,

    /// <summary>カプセル。大きさの X が半径、Y が円柱の部分の高さ（Y 軸の向き）。</summary>
    Capsule = 2,
}

public enum PhysicsBodyKind : uint
{
    /// <summary>外から置き方を与える（ボーンに追従）。</summary>
    Kinematic = 0,

    /// <summary>重力とジョイントで動く。</summary>
    Dynamic = 1,
}

/// <summary>剛体を足すときの中身（tp_physics.h の TpBodyDesc と同じ並び）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsBodyDesc
{
    public PhysicsShape Shape;
    public PhysicsBodyKind Kind;
    public Vector3 Size;
    public float Mass;
    public PhysicsTransform Transform;
    public float LinearDamping;
    public float AngularDamping;
    public float Restitution;
    public float Friction;
    /// <summary>自分のグループのビット（1 &lt;&lt; 番号）。</summary>
    public uint Group;
    /// <summary>ぶつかる相手のグループのビット。</summary>
    public uint Mask;
}

/// <summary>6 自由度のばね付きジョイント（tp_physics.h の TpJointDesc と同じ並び）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsJointDesc
{
    public int BodyA;
    public int BodyB;
    public PhysicsTransform Transform;
    public Vector3 LinearLower;
    public Vector3 LinearUpper;
    public Vector3 AngularLower;
    public Vector3 AngularUpper;
    public Vector3 LinearStiffness;
    public Vector3 AngularStiffness;
}

[Flags]
public enum PhysicsImpulseFlags : uint
{
    None = 0,

    /// <summary>勢いを剛体の向きで回してから与える。</summary>
    Local = 1,

    /// <summary>先に剛体の動き（速さ・回転の速さ）を 0 にする。</summary>
    Stop = 2,
}

/// <summary>剛体 1 つに与える勢い（tp_physics.h の TpImpulse と同じ並び）。外から置き方を与える剛体には効かない。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsImpulse
{
    public int Body;
    public PhysicsImpulseFlags Flags;
    /// <summary>移動の勢い（重さで割った分だけ速さが変わる）。</summary>
    public Vector3 Linear;
    /// <summary>回転の勢い。</summary>
    public Vector3 Torque;
}

/// <summary>物理の C API が失敗を返したときの例外。</summary>
public sealed class PhysicsException(int result, string message) : Exception($"{message}（{result}）")
{
    public int Result { get; } = result;
}

/// <summary>
/// 物理の世界 1 つ（Bullet）。剛体は足した順に 0, 1, 2… の番号で、置き方の受け渡しは全剛体分の配列で行う。
/// 同時には 1 つのスレッドからだけ使う。使い終わったら <see cref="Dispose"/> する。
/// </summary>
public sealed unsafe partial class PhysicsWorld : IDisposable
{
    public const int ExpectedApiVersionMajor = 0;
    public const int ExpectedApiVersionMinor = 4;

    private const string Library = "3DTheater.Physics.Native";
    private nint _handle;

    private PhysicsWorld(nint handle) => _handle = handle;

    public bool IsDisposed => _handle == 0;

    public int BodyCount => tp_world_body_count(Handle);

    private nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    public static (int Major, int Minor) ApiVersion
    {
        get
        {
            uint version = tp_get_api_version();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }
    }

    public static PhysicsWorld Create(Vector3 gravity)
    {
        var (major, minor) = ApiVersion;
        if (major != ExpectedApiVersionMajor || minor < ExpectedApiVersionMinor)
        {
            throw new PhysicsException(-4, $"物理の DLL の版が合いません（DLL: {major}.{minor}、必要: {ExpectedApiVersionMajor}.{ExpectedApiVersionMinor} 以上）");
        }
        var desc = new TpWorldDesc { StructSize = (uint)sizeof(TpWorldDesc), Gravity = gravity };
        nint handle;
        Check(tp_world_create(&desc, &handle));
        return new PhysicsWorld(handle);
    }

    public void AddBodies(ReadOnlySpan<PhysicsBodyDesc> bodies)
    {
        fixed (PhysicsBodyDesc* p = bodies)
        {
            Check(tp_world_add_bodies(Handle, p, bodies.Length));
        }
    }

    public void AddJoints(ReadOnlySpan<PhysicsJointDesc> joints)
    {
        fixed (PhysicsJointDesc* p = joints)
        {
            Check(tp_world_add_joints(Handle, p, joints.Length));
        }
    }

    public void SetGravity(Vector3 gravity) => Check(tp_world_set_gravity(Handle, &gravity));

    /// <summary>
    /// 障害物（ほかのモデルの体など、外から置き方を与える剛体）を置き直す（前の障害物は外す）。剛体の数・番号には入らない。
    /// <see cref="PhysicsBodyDesc.Kind"/>・<see cref="PhysicsBodyDesc.Mass"/> は使わない。
    /// </summary>
    public void SetObstacles(ReadOnlySpan<PhysicsBodyDesc> obstacles)
    {
        fixed (PhysicsBodyDesc* p = obstacles)
        {
            Check(tp_world_set_obstacles(Handle, p, obstacles.Length));
        }
    }

    /// <summary>
    /// 障害物の目標の置き方（数は障害物の数と同じ）。<paramref name="snap"/> ならすぐ置き、そうでなければ次の <see cref="Step"/> で
    /// 前の目標から少しずつ動かす。
    /// </summary>
    public void SetObstacleTargets(ReadOnlySpan<PhysicsTransform> targets, bool snap)
    {
        fixed (PhysicsTransform* p = targets)
        {
            Check(tp_world_set_obstacle_targets(Handle, p, targets.Length, snap ? 1 : 0));
        }
    }

    /// <summary>床を置く・置き直す・外す（<see cref="PhysicsFloor.Enabled"/> が false なら外す）。動く剛体だけが当たる。</summary>
    public void SetFloor(PhysicsFloor floor)
    {
        var desc = new TpFloorDesc
        {
            StructSize = (uint)sizeof(TpFloorDesc),
            Enabled = floor.Enabled ? 1u : 0u,
            Point = floor.Point,
            Normal = floor.Normal,
            Friction = floor.Friction,
            Restitution = floor.Restitution,
        };
        Check(tp_world_set_floor(Handle, &desc));
    }

    /// <summary>全剛体を与えた置き方へ動かし、速さを 0 にする。</summary>
    public void Reset(ReadOnlySpan<PhysicsTransform> transforms)
    {
        fixed (PhysicsTransform* p = transforms)
        {
            Check(tp_world_reset(Handle, p, transforms.Length));
        }
    }

    /// <summary>
    /// <paramref name="seconds"/> 秒進める。<paramref name="substeps"/> 回に分けて計算し、外から置き方を与える剛体は、
    /// 前の目標から <paramref name="targets"/> まで少しずつ動かす（動く剛体の分は使わない）。
    /// </summary>
    public void Step(ReadOnlySpan<PhysicsTransform> targets, float seconds, int substeps)
    {
        fixed (PhysicsTransform* p = targets)
        {
            Check(tp_world_step(Handle, p, targets.Length, seconds, substeps));
        }
    }

    public void GetTransforms(Span<PhysicsTransform> destination)
    {
        fixed (PhysicsTransform* p = destination)
        {
            Check(tp_world_get_transforms(Handle, p, destination.Length));
        }
    }

    /// <summary>剛体に勢いを与える（次の <see cref="Step"/> の前に）。並んだ順に、止める → 勢いを足す、を行う。</summary>
    public void ApplyImpulses(ReadOnlySpan<PhysicsImpulse> impulses)
    {
        if (impulses.IsEmpty)
        {
            return;
        }
        fixed (PhysicsImpulse* p = impulses)
        {
            Check(tp_world_apply_impulses(Handle, p, impulses.Length));
        }
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        tp_world_destroy(_handle);
        _handle = 0;
    }

    private static void Check(int result)
    {
        if (result >= 0)
        {
            return;
        }
        char* buffer = stackalloc char[512];
        int length = tp_get_last_error_message(buffer, 512);
        string message = length > 0 ? new string(buffer, 0, Math.Min(length, 511)) : "物理演算でエラーが発生しました";
        throw new PhysicsException(result, message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpWorldDesc
    {
        public uint StructSize;
        public Vector3 Gravity;
    }

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint tp_get_api_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_get_last_error_message(char* buffer, int capacity);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_create(TpWorldDesc* desc, nint* world);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_destroy(nint world);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_add_bodies(nint world, PhysicsBodyDesc* bodies, int count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_add_joints(nint world, PhysicsJointDesc* joints, int count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_set_gravity(nint world, Vector3* gravity);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_body_count(nint world);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_reset(nint world, PhysicsTransform* transforms, int count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_step(nint world, PhysicsTransform* targets, int count, float timeStep, int substeps);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_get_transforms(nint world, PhysicsTransform* transforms, int count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_apply_impulses(nint world, PhysicsImpulse* impulses, int count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_set_floor(nint world, TpFloorDesc* floor);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_set_obstacles(nint world, PhysicsBodyDesc* obstacles, int count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tp_world_set_obstacle_targets(nint world, PhysicsTransform* targets, int count, int snap);

    [StructLayout(LayoutKind.Sequential)]
    private struct TpFloorDesc
    {
        public uint StructSize;
        public uint Enabled;
        public Vector3 Point;
        public Vector3 Normal;
        public float Friction;
        public float Restitution;
    }
}

/// <summary>
/// 物理の床（無限に広い平面）。<see cref="Point"/> を通り、<see cref="Normal"/> の向きが表（剛体を押し返す側）。
/// 座標は物理の世界の座標（モデルの座標）。
/// </summary>
public readonly record struct PhysicsFloor(bool Enabled, Vector3 Point, Vector3 Normal, float Friction = 0.5f, float Restitution = 0f)
{
    /// <summary>床なし。</summary>
    public static PhysicsFloor None { get; } = new(false, Vector3.Zero, Vector3.UnitY);

    // 置き方の写し（シーンの座標 ↔ モデルの座標）は ModelPlacement

    /// <summary>
    /// シーンの床（高さ 0 の水平な面）を、モデルの座標（物理の世界の座標）で表したもの。モデルは「拡大 → 回転 → 移動」の順に置く
    /// （シーンの決まり）。拡大は 1 つの倍率で直す（縦横で違う拡大は考えない）。
    /// </summary>
    public static PhysicsFloor SceneFloorInModel(Quaternion modelRotation, Vector3 modelPosition, float modelScale)
    {
        Quaternion inverse = Quaternion.Inverse(Quaternion.Normalize(modelRotation));
        float scale = MathF.Abs(modelScale) > 1e-6f && float.IsFinite(modelScale) ? modelScale : 1f;
        Vector3 point = Vector3.Transform(new Vector3(0f, -modelPosition.Y, 0f), inverse) / scale;
        return new PhysicsFloor(true, point, Vector3.Transform(Vector3.UnitY, inverse));
    }
}

/// <summary>
/// モデルの置き方（シーンの決まりで「拡大 → 回転 → 移動」。拡大は 1 つの倍率）。物理はモデルの座標で計算するので、
/// ほかのモデルの剛体をこのモデルの座標へ直すのに使う。
/// </summary>
public readonly record struct ModelPlacement(Vector3 Position, Quaternion Rotation, float Scale)
{
    private float SafeScale => MathF.Abs(Scale) > 1e-6f && float.IsFinite(Scale) ? Scale : 1f;

    public Vector3 ToScene(Vector3 point) => Vector3.Transform(point * SafeScale, Quaternion.Normalize(Rotation)) + Position;

    public Vector3 FromScene(Vector3 point) => Vector3.Transform(point - Position, Quaternion.Inverse(Quaternion.Normalize(Rotation))) / SafeScale;

    /// <summary><paramref name="from"/> のモデルの座標の置き方を、このモデルの座標へ直す。</summary>
    public PhysicsTransform FromModel(in PhysicsTransform transform, in ModelPlacement from) => new(
        FromScene(from.ToScene(transform.Position)),
        Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(transform.Rotation, Quaternion.Normalize(from.Rotation)),
            Quaternion.Inverse(Quaternion.Normalize(Rotation)))));

    /// <summary><paramref name="from"/> のモデルの長さ 1 が、このモデルの座標でいくつになるか。</summary>
    public float ScaleFrom(in ModelPlacement from) => from.SafeScale / SafeScale;
}
