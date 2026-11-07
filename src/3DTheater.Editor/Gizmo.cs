using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>ギズモの方式（指示書 2.6）。切り替えてもモデルの位置・姿勢・キーは変えない。</summary>
public enum GizmoStyle
{
    /// <summary>選んだ物に、移動の矢印と回転の輪を一緒に出す（MMD のボーン操作に近い）。</summary>
    Mmd,

    /// <summary>ツールバーで選んだ道具（移動・回転）の取っ手だけを出す（Blender の XYZ 軸ギズモに近い）。</summary>
    Blender,
}

/// <summary>Blender 方式で使う道具。</summary>
public enum GizmoTool
{
    Select,
    Move,
    Rotate,
}

public enum GizmoMode
{
    Move,
    Rotate,
}

/// <summary>ギズモの取っ手 1 つ（移動の矢印か回転の輪の、どの軸か。軸は 0 = X、1 = Y、2 = Z）。</summary>
public readonly record struct GizmoHandle(GizmoMode Mode, int Axis);

/// <summary>ギズモを置く場所と向き（ワールドの座標）。<see cref="Size"/> は矢印の長さ・輪の半径（画面での大きさが一定になるよう決める）。</summary>
public readonly record struct GizmoFrame(Vector3 Origin, Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ, float Size)
{
    public Vector3 Axis(int index) => index switch { 0 => AxisX, 1 => AxisY, _ => AxisZ };

    public static GizmoFrame Create(Vector3 origin, Quaternion orientation, float size) =>
        new(origin, Vector3.Transform(Vector3.UnitX, orientation), Vector3.Transform(Vector3.UnitY, orientation),
            Vector3.Transform(Vector3.UnitZ, orientation), size);
}

/// <summary>ギズモの設定（全プレビューで共通）。変わると <see cref="Changed"/> を出す。</summary>
public sealed class GizmoSettings
{
    private GizmoStyle _style = GizmoStyle.Mmd;
    private GizmoTool _tool = GizmoTool.Select;
    private bool _localSpace;

    public event EventHandler? Changed;

    public GizmoStyle Style
    {
        get => _style;
        set => Set(ref _style, value);
    }

    public GizmoTool Tool
    {
        get => _tool;
        set => Set(ref _tool, value);
    }

    /// <summary>true ならローカル（選んだ物の向き）の軸、false ならワールドの軸。</summary>
    public bool LocalSpace
    {
        get => _localSpace;
        set => Set(ref _localSpace, value);
    }

    /// <summary>移動のスナップ（Ctrl を押しながら）。</summary>
    public float MoveSnap { get; set; } = 0.5f;

    /// <summary>回転のスナップ（度、Ctrl を押しながら）。</summary>
    public float RotateSnapDegrees { get; set; } = 5f;

    /// <summary>今の方式と道具で、移動・回転の取っ手を出すか（物が動かせるかは別に見る）。</summary>
    public bool Shows(GizmoMode mode) => _style == GizmoStyle.Mmd || (mode == GizmoMode.Move ? _tool == GizmoTool.Move : _tool == GizmoTool.Rotate);

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// ギズモの計算（形・当たり判定・ドラッグの量・ボーンや物の値への直し方）。画面には依存しない。
/// 当たり判定は画面の上（ピクセル）で行い、ドラッグの量は視線と軸・面の交わりから求める。
/// </summary>
public static class GizmoMath
{
    public const int RingSegments = 48;
    private const float ArrowHeadLength = 0.18f;
    private const float ArrowHeadWidth = 0.06f;

    /// <summary>画面で <paramref name="pixels"/> ピクセルになる、<paramref name="origin"/> の場所での長さ。</summary>
    public static float WorldSizeForPixels(Vector3 origin, Matrix4x4 view, Matrix4x4 projection, Vector2 viewportSize, float pixels)
    {
        if (!Matrix4x4.Invert(view, out Matrix4x4 inverseView))
        {
            return 1f;
        }
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inverseView));
        Matrix4x4 viewProjection = view * projection;
        if (!Picking.TryProject(origin, viewProjection, viewportSize, out Vector2 a) || !Picking.TryProject(origin + up, viewProjection, viewportSize, out Vector2 b))
        {
            return 1f;
        }
        float pixelsPerUnit = Vector2.Distance(a, b);
        return pixelsPerUnit < 1e-6f ? 1f : pixels / pixelsPerUnit;
    }

    /// <summary>取っ手の形（線分の並び。描くときと当たり判定に使う）。矢印は軸と先の小さな四角すい、輪は軸まわりの円。</summary>
    public static IEnumerable<(Vector3 A, Vector3 B)> Segments(GizmoFrame frame, GizmoHandle handle)
    {
        Vector3 axis = frame.Axis(handle.Axis);
        Vector3 u = frame.Axis((handle.Axis + 1) % 3);
        Vector3 v = frame.Axis((handle.Axis + 2) % 3);
        if (handle.Mode == GizmoMode.Move)
        {
            Vector3 tip = frame.Origin + (axis * frame.Size);
            Vector3 neck = frame.Origin + (axis * frame.Size * (1 - ArrowHeadLength));
            float w = frame.Size * ArrowHeadWidth;
            yield return (frame.Origin + (axis * frame.Size * 0.12f), tip);
            Vector3[] corners = [neck + (u * w), neck + (v * w), neck - (u * w), neck - (v * w)];
            for (int i = 0; i < 4; i++)
            {
                yield return (corners[i], tip);
                yield return (corners[i], corners[(i + 1) % 4]);
            }
            yield break;
        }
        float radius = frame.Size;
        Vector3 previous = frame.Origin + (u * radius);
        for (int i = 1; i <= RingSegments; i++)
        {
            float angle = i * MathF.Tau / RingSegments;
            Vector3 point = frame.Origin + (((u * MathF.Cos(angle)) + (v * MathF.Sin(angle))) * radius);
            yield return (previous, point);
            previous = point;
        }
    }

    /// <summary>
    /// 画面上の点に最も近い取っ手（<paramref name="tolerance"/> ピクセル以内）。無ければ null。
    /// 矢印は軸の線、輪は円の線との距離で調べる。
    /// </summary>
    public static GizmoHandle? HitTest(GizmoFrame frame, IEnumerable<GizmoHandle> handles, Vector2 point, Matrix4x4 viewProjection, Vector2 viewportSize, float tolerance)
    {
        GizmoHandle? best = null;
        float bestDistance = tolerance;
        foreach (GizmoHandle handle in handles)
        {
            foreach (var (a, b) in Segments(frame, handle).Take(handle.Mode == GizmoMode.Move ? 1 : RingSegments))
            {
                if (!Picking.TryProject(a, viewProjection, viewportSize, out Vector2 sa) || !Picking.TryProject(b, viewProjection, viewportSize, out Vector2 sb))
                {
                    continue;
                }
                float distance = Picking.DistanceToSegment(point, sa, sb);
                // 矢印は先の方もつかみやすいよう、少し広く取る
                if (distance <= bestDistance)
                {
                    best = handle;
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 視線が軸の線に最も近づく所の、軸に沿った位置（<paramref name="origin"/> からの長さ）。
    /// 軸が視線とほぼ平行なら null（そのときは画面の上での動きで求める）。
    /// </summary>
    public static float? AxisParameter(Vector3 origin, Vector3 axis, Ray ray)
    {
        // 2 本の直線（軸: origin + axis t、視線: ray.Origin + ray.Direction s）の最も近い点
        Vector3 w = origin - ray.Origin;
        float a = Vector3.Dot(axis, axis);
        float b = Vector3.Dot(axis, ray.Direction);
        float c = Vector3.Dot(ray.Direction, ray.Direction);
        float d = Vector3.Dot(axis, w);
        float e = Vector3.Dot(ray.Direction, w);
        float denominator = (a * c) - (b * b);
        if (denominator < 1e-4f)
        {
            return null;
        }
        return ((b * e) - (c * d)) / denominator;
    }

    /// <summary>
    /// 移動の矢印をドラッグしたときの、軸に沿った移動の量。視線と軸から求め、軸が視線に近いときは画面の上の動きで求める。
    /// </summary>
    public static float MoveAmount(GizmoFrame frame, int axisIndex, Ray startRay, Ray currentRay, Vector2 startPoint, Vector2 currentPoint, Matrix4x4 viewProjection, Vector2 viewportSize)
    {
        Vector3 axis = frame.Axis(axisIndex);
        if (Picking.TryProject(frame.Origin, viewProjection, viewportSize, out Vector2 s0)
            && Picking.TryProject(frame.Origin + (axis * frame.Size), viewProjection, viewportSize, out Vector2 s1))
        {
            Vector2 screenAxis = s1 - s0;
            float screenLength = screenAxis.Length();
            if (screenLength > 12f && AxisParameter(frame.Origin, axis, startRay) is float t0 && AxisParameter(frame.Origin, axis, currentRay) is float t1)
            {
                return t1 - t0;
            }
            if (screenLength > 1e-3f)
            {
                // 軸がこちらを向いているときは、画面に写った軸の向きへの動きで決める
                return Vector2.Dot(currentPoint - startPoint, screenAxis) / (screenLength * screenLength) * frame.Size;
            }
        }
        return 0f;
    }

    /// <summary>
    /// 回転の輪をドラッグしたときの、前の位置からの回転の角度（ラジアン、軸の向きに右回りが正）。
    /// 輪の面と視線の交わりから求め、面が真横を向いているときは輪の接線の向きへの動きで求める。
    /// </summary>
    public static float RotateAmount(GizmoFrame frame, int axisIndex, Ray previousRay, Ray currentRay, Vector2 previousPoint, Vector2 currentPoint,
        Vector2 grabTangent)
    {
        Vector3 axis = frame.Axis(axisIndex);
        if (MathF.Abs(Vector3.Dot(Vector3.Normalize(currentRay.Direction), axis)) > 0.15f
            && IntersectPlane(frame.Origin, axis, previousRay) is Vector3 p0 && IntersectPlane(frame.Origin, axis, currentRay) is Vector3 p1)
        {
            Vector3 v0 = p0 - frame.Origin;
            Vector3 v1 = p1 - frame.Origin;
            if (v0.LengthSquared() > 1e-10f && v1.LengthSquared() > 1e-10f)
            {
                return MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(v0, v1)), Vector3.Dot(v0, v1));
            }
        }
        // 輪を真横から見ているとき: つかんだ所の接線（画面の上）に沿った動き 100 ピクセルで 90 度
        float length = grabTangent.Length();
        return length < 1e-6f ? 0f : Vector2.Dot(currentPoint - previousPoint, grabTangent / length) * (MathF.PI / 2f / 100f);
    }

    /// <summary>
    /// 輪のつかんだ所での、回転が正になる向き（画面の上の向き、ピクセル）。
    /// その向きが画面の奥を向いて見えないとき（輪を真横から見ているとき）は、画面に写った軸に直角な向きを使う。
    /// </summary>
    public static Vector2 RingTangent(GizmoFrame frame, int axisIndex, Vector3 grabPoint, Matrix4x4 viewProjection, Vector2 viewportSize)
    {
        Vector3 axis = frame.Axis(axisIndex);
        Vector3 tangent = Vector3.Cross(axis, grabPoint - frame.Origin);
        if (tangent.LengthSquared() > 1e-12f
            && Picking.TryProject(grabPoint, viewProjection, viewportSize, out Vector2 a)
            && Picking.TryProject(grabPoint + (Vector3.Normalize(tangent) * frame.Size * 0.1f), viewProjection, viewportSize, out Vector2 b)
            && Vector2.Distance(a, b) > 2f)
        {
            return b - a;
        }
        if (Picking.TryProject(frame.Origin, viewProjection, viewportSize, out Vector2 o)
            && Picking.TryProject(frame.Origin + (axis * frame.Size), viewProjection, viewportSize, out Vector2 e)
            && Vector2.Distance(o, e) > 1e-3f)
        {
            Vector2 d = Vector2.Normalize(e - o);
            return new Vector2(-d.Y, d.X) * 10f;
        }
        return Vector2.Zero;
    }

    /// <summary>輪の上で、画面上の点に最も近い所（つかんだ所）。</summary>
    public static Vector3 NearestRingPoint(GizmoFrame frame, int axisIndex, Vector2 point, Matrix4x4 viewProjection, Vector2 viewportSize)
    {
        Vector3 best = frame.Origin;
        float bestDistance = float.MaxValue;
        foreach (var (a, _) in Segments(frame, new GizmoHandle(GizmoMode.Rotate, axisIndex)))
        {
            if (Picking.TryProject(a, viewProjection, viewportSize, out Vector2 s) && Vector2.Distance(s, point) < bestDistance)
            {
                bestDistance = Vector2.Distance(s, point);
                best = a;
            }
        }
        return best;
    }

    public static Vector3? IntersectPlane(Vector3 origin, Vector3 normal, Ray ray)
    {
        float denominator = Vector3.Dot(normal, ray.Direction);
        if (MathF.Abs(denominator) < 1e-6f)
        {
            return null;
        }
        float t = Vector3.Dot(origin - ray.Origin, normal) / denominator;
        return t < 0 ? null : ray.Origin + (ray.Direction * t);
    }

    public static float Snap(float value, float step) => step <= 0 ? value : MathF.Round(value / step) * step;

    /// <summary>
    /// ボーンを、ワールドの座標で <paramref name="worldDelta"/> だけ動かしたときの、ボーンの移動の値（親から見た向き）。
    /// <paramref name="parentWorldRotation"/> は親のボーンのワールドでの向き（モデルの回転を含む）、<paramref name="modelScale"/> はモデルの拡大。
    /// </summary>
    public static Vector3 BoneTranslation(Vector3 startTranslation, Vector3 worldDelta, Quaternion parentWorldRotation, Vector3 modelScale)
    {
        Vector3 inParent = Vector3.Transform(worldDelta, Quaternion.Inverse(parentWorldRotation));
        Vector3 scale = new(Safe(modelScale.X), Safe(modelScale.Y), Safe(modelScale.Z));
        return startTranslation + (inParent / scale);

        static float Safe(float s) => MathF.Abs(s) < 1e-6f ? 1f : s;
    }

    /// <summary>
    /// ボーンを、ワールドの軸 <paramref name="worldAxis"/> まわりに <paramref name="angle"/> 回したときの、ボーンの回転の値（親から見た向き）。
    /// </summary>
    public static Quaternion BoneRotation(Quaternion startLocal, Vector3 worldAxis, float angle, Quaternion parentWorldRotation)
    {
        Quaternion world = Quaternion.CreateFromAxisAngle(Vector3.Normalize(worldAxis), angle);
        // 親の向きの中で見た回転にして、今の回転の外側から掛ける
        Quaternion inParent = Quaternion.Inverse(parentWorldRotation) * world * parentWorldRotation;
        return Quaternion.Normalize(inParent * startLocal);
    }

    /// <summary>シーンの物（モデル・カメラなど）を、ワールドの軸まわりに回したときの回転（度）。</summary>
    public static Vector3 NodeRotation(Vector3 startDegrees, Vector3 worldAxis, float angle)
    {
        Quaternion world = Quaternion.CreateFromAxisAngle(Vector3.Normalize(worldAxis), angle);
        return SceneRotation.ToDegrees(Quaternion.Normalize(world * SceneRotation.ToQuaternion(startDegrees)));
    }
}
