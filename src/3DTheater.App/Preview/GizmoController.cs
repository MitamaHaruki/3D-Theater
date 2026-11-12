using System.Numerics;
using System.Windows.Input;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Preview;

/// <summary>重ね描きの線をためる入れ物（毎フレーム使い回す）。</summary>
internal sealed class LineBuffer
{
    private LineVertex[] _lines = new LineVertex[512];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Add(Vector3 a, Vector3 b, Vector4 color)
    {
        if (Count + 2 > _lines.Length)
        {
            Array.Resize(ref _lines, _lines.Length * 2);
        }
        _lines[Count++] = new LineVertex(a, color);
        _lines[Count++] = new LineVertex(b, color);
    }

    public ReadOnlyMemory<LineVertex> Memory => _lines.AsMemory(0, Count);
}

/// <summary>プレビューの 1 区画のギズモ（描画とマウスでの操作）。どの区画でも同じ設定（<see cref="EditorContext.Gizmo"/>）を使う。</summary>
internal sealed class GizmoController(EditorContext context)
{
    private const float ArrowPixels = 90;
    private const float HitTolerance = 7;
    private static readonly Vector4[] AxisColors = [new(0.95f, 0.32f, 0.32f, 1), new(0.38f, 0.85f, 0.42f, 1), new(0.32f, 0.52f, 1f, 1)];
    private static readonly Vector4 ActiveColor = new(1f, 0.88f, 0.2f, 1);

    private GizmoHandle? _hover;
    private Drag? _drag;

    public bool IsDragging => _drag is not null;

    /// <summary>今のギズモで動かすもの。</summary>
    private sealed record Target(SceneNode Node, Vector3 Origin, Quaternion Orientation, bool CanMove, bool CanRotate,
        Quaternion ParentWorldRotation, Vector3 ModelScale);

    private sealed class Drag
    {
        public required Target Target { get; init; }
        public required GizmoHandle Handle { get; init; }
        public required GizmoFrame Frame { get; init; }
        public required EditTransaction Transaction { get; init; }
        public required System.Windows.Point StartPoint { get; init; }
        public required Ray StartRay { get; init; }
        public required Vector3 StartPosition { get; init; }
        public required Vector3 StartDegrees { get; init; }
        public required Quaternion StartOrientation { get; init; }
        public Vector2 Tangent { get; init; }
        public System.Windows.Point PreviousPoint { get; set; }
        public Ray PreviousRay { get; set; }
        public float Angle { get; set; }
    }

    /// <summary>区画の見え方（視点の行列と大きさ）。</summary>
    public readonly record struct ViewInfo(Matrix4x4 View, Matrix4x4 Projection, Vector2 Size)
    {
        public Matrix4x4 ViewProjection => View * Projection;

        public Ray RayAt(System.Windows.Point p) => Picking.CreateRay(new Vector2((float)p.X, (float)p.Y), Size, View, Projection);
    }

    /// <summary>ギズモの線を足す（描画のたびに呼ぶ）。</summary>
    public void Append(LineBuffer lines, ViewInfo view)
    {
        if (CurrentTarget() is not { } target)
        {
            return;
        }
        foreach (var (handle, frame) in Handles(target, view))
        {
            bool active = _drag is { } d ? d.Handle == handle : _hover == handle;
            if (_drag is not null && !active)
            {
                continue; // ドラッグ中は使っている取っ手だけを出す
            }
            Vector4 color = active ? ActiveColor : AxisColors[handle.Axis];
            foreach (var (a, b) in GizmoMath.Segments(frame, handle))
            {
                lines.Add(a, b, color);
            }
        }
    }

    /// <summary>マウスの下の取っ手を調べる。変わったら true（描き直す）。</summary>
    public bool UpdateHover(System.Windows.Point point, ViewInfo view)
    {
        GizmoHandle? hit = HitTest(point, view);
        if (hit == _hover)
        {
            return false;
        }
        _hover = hit;
        return true;
    }

    public void ClearHover() => _hover = null;

    /// <summary>取っ手の上なら、ドラッグを始めて true を返す。</summary>
    public bool TryBeginDrag(System.Windows.Point point, ViewInfo view)
    {
        context.Poses.UpdateDirtyModels(); // ボーンの今の位置で調べる
        if (CurrentTarget() is not { } target || HitTest(point, view, target) is not { } handle)
        {
            return false;
        }
        context.Playback.Pause(); // 再生中の編集は止めてから（指示書 3.4）
        GizmoFrame frame = Handles(target, view).First(h => h.Handle == handle).Frame;
        Ray ray = view.RayAt(point);
        var start = new Vector2((float)point.X, (float)point.Y);
        Vector2 tangent = handle.Mode == GizmoMode.Rotate
            ? GizmoMath.RingTangent(frame, handle.Axis, GizmoMath.NearestRingPoint(frame, handle.Axis, start, view.ViewProjection, view.Size), view.ViewProjection, view.Size)
            : Vector2.Zero;
        string what = handle.Mode == GizmoMode.Move ? (target.Node is BoneNode ? "移動" : "位置") : "回転";
        _drag = new Drag
        {
            Target = target,
            Handle = handle,
            Frame = frame,
            Transaction = context.Edits.Begin($"{target.Node.Name}の{what}（ギズモ）", [target.Node]),
            StartPoint = point,
            StartRay = ray,
            StartPosition = target.Node.Position,
            StartDegrees = target.Node.Rotation,
            StartOrientation = target.Node is BoneNode bone ? bone.Orientation : Quaternion.Identity,
            Tangent = tangent,
            PreviousPoint = point,
            PreviousRay = ray,
        };
        _hover = handle;
        return true;
    }

    /// <summary>ドラッグ中のマウスの動き。Ctrl でスナップ（移動 0.5・回転 5°）。</summary>
    public void Move(System.Windows.Point point, ViewInfo view)
    {
        if (_drag is not { } d)
        {
            return;
        }
        bool snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        Ray ray = view.RayAt(point);
        Vector3 axis = d.Frame.Axis(d.Handle.Axis);
        SceneNode node = d.Target.Node;
        if (d.Handle.Mode == GizmoMode.Move)
        {
            float amount = GizmoMath.MoveAmount(d.Frame, d.Handle.Axis, d.StartRay, ray, ToVector(d.StartPoint), ToVector(point), view.ViewProjection, view.Size);
            if (snap)
            {
                amount = GizmoMath.Snap(amount, context.Gizmo.MoveSnap);
            }
            Vector3 delta = axis * amount;
            node.Position = node is BoneNode or ModelNode { Attachment: not null }
                ? GizmoMath.BoneTranslation(d.StartPosition, delta, d.Target.ParentWorldRotation, d.Target.ModelScale)
                : d.StartPosition + delta;
        }
        else
        {
            d.Angle += GizmoMath.RotateAmount(d.Frame, d.Handle.Axis, d.PreviousRay, ray, ToVector(d.PreviousPoint), ToVector(point), d.Tangent);
            d.PreviousRay = ray;
            d.PreviousPoint = point;
            float angle = snap ? GizmoMath.Snap(d.Angle, context.Gizmo.RotateSnapDegrees * MathF.PI / 180f) : d.Angle;
            if (node is BoneNode bone)
            {
                bone.Orientation = GizmoMath.BoneRotation(d.StartOrientation, axis, angle, d.Target.ParentWorldRotation);
            }
            else if (node is ModelNode { Attachment: not null })
            {
                node.Rotation = SceneRotation.ToDegrees(
                    GizmoMath.BoneRotation(SceneRotation.ToQuaternion(d.StartDegrees), axis, angle, d.Target.ParentWorldRotation));
            }
            else
            {
                node.Rotation = GizmoMath.NodeRotation(d.StartDegrees, axis, angle);
            }
        }
    }

    /// <summary>ドラッグを終える（1 つの操作として残す）。</summary>
    public void EndDrag()
    {
        _drag?.Transaction.Commit();
        _drag = null;
    }

    /// <summary>ドラッグをやめて、始める前に戻す（右クリック・Esc）。</summary>
    public bool CancelDrag()
    {
        if (_drag is null)
        {
            return false;
        }
        _drag.Transaction.Cancel();
        _drag = null;
        return true;
    }

    // モデルのワールドでの位置・向き・拡大（付けていなければ、モデルの値そのもの）
    private static (Vector3 Position, Quaternion Rotation, Vector3 Scale) WorldTransform(ModelNode model)
    {
        if (model.Attachment is null)
        {
            return (model.Position, SceneRotation.ToQuaternion(model.Rotation), model.Scale);
        }
        Matrix4x4.Decompose(model.WorldMatrix, out Vector3 scale, out Quaternion rotation, out Vector3 position);
        return (position, Quaternion.Normalize(rotation), scale);
    }

    /// <summary>今どの区画でもギズモを出せる物（選んでいる物が 1 つで、動かせて、見えているとき）。</summary>
    private Target? CurrentTarget()
    {
        if (context.Selection.Items.Count != 1 || context.Selection.Primary is not { } node || !node.CanEdit || !node.IsEffectivelyVisible)
        {
            return null;
        }
        GizmoSettings settings = context.Gizmo;
        if (!settings.Shows(GizmoMode.Move) && !settings.Shows(GizmoMode.Rotate))
        {
            return null;
        }
        switch (node)
        {
            case BoneNode bone when bone.Model is { } model && context.GetLoaded(model) is { } loaded:
            {
                Matrix4x4 world = model.WorldMatrix;
                // ボーンに付けたモデルのボーンは、付けた先に合わせた向き・拡大で動かす
                var (_, modelRotation, modelScale) = WorldTransform(model);
                int parent = loaded.Rig.Bones[bone.Index].Parent;
                Quaternion parentWorld = parent >= 0 ? Quaternion.Normalize(modelRotation * loaded.State.GlobalRotation[parent]) : modelRotation;
                Quaternion boneWorld = Quaternion.Normalize(modelRotation * loaded.State.GlobalRotation[bone.Index]);
                return new Target(bone, Vector3.Transform(loaded.State.BonePosition(bone.Index), world), boneWorld,
                    bone.Traits.CanTranslate, bone.Traits.CanRotate, parentWorld, modelScale);
            }
            case BoneNode or MorphNode:
                return null;
            case ModelNode { Attachment: not null } attached:
            {
                // ボーンに付けたモデル: 付けた先のボーンの中で動かす（ボーンと同じ計算）
                Matrix4x4.Decompose(attached.ParentMatrix, out Vector3 parentScale, out Quaternion parentRotation, out _);
                parentRotation = Quaternion.Normalize(parentRotation);
                return new Target(attached, attached.WorldMatrix.Translation,
                    Quaternion.Normalize(parentRotation * SceneRotation.ToQuaternion(attached.Rotation)), true, true, parentRotation, parentScale);
            }
            case { HasTransform: true }:
                return new Target(node, node.Position, SceneRotation.ToQuaternion(node.Rotation), true, true, Quaternion.Identity, Vector3.One);
            default:
                return null;
        }
    }

    // 出す取っ手と、その形の置き場所。当たり判定で矢印が勝つよう、輪を先に並べる
    private IEnumerable<(GizmoHandle Handle, GizmoFrame Frame)> Handles(Target target, ViewInfo view)
    {
        GizmoSettings settings = context.Gizmo;
        float size = GizmoMath.WorldSizeForPixels(target.Origin, view.View, view.Projection, view.Size, ArrowPixels);
        Quaternion axes = settings.LocalSpace ? target.Orientation : Quaternion.Identity;
        bool showMove = target.CanMove && settings.Shows(GizmoMode.Move);
        bool showRotate = target.CanRotate && settings.Shows(GizmoMode.Rotate);
        // MMD 方式で両方出すときは、矢印の先が輪の外に出るよう輪を少し小さくする
        float ringSize = showMove ? size * 0.72f : size;
        if (showRotate)
        {
            GizmoFrame ring = GizmoFrame.Create(target.Origin, axes, ringSize);
            for (int axis = 0; axis < 3; axis++)
            {
                yield return (new GizmoHandle(GizmoMode.Rotate, axis), ring);
            }
        }
        if (showMove)
        {
            GizmoFrame arrows = GizmoFrame.Create(target.Origin, axes, size);
            for (int axis = 0; axis < 3; axis++)
            {
                yield return (new GizmoHandle(GizmoMode.Move, axis), arrows);
            }
        }
    }

    private GizmoHandle? HitTest(System.Windows.Point point, ViewInfo view, Target? target = null)
    {
        target ??= CurrentTarget();
        if (target is null)
        {
            return null;
        }
        GizmoHandle? best = null;
        float bestDistance = float.MaxValue;
        foreach (var (handle, frame) in Handles(target, view))
        {
            if (GizmoMath.HitTest(frame, [handle], ToVector(point), view.ViewProjection, view.Size, HitTolerance) is not null)
            {
                // 同じくらい近ければ、後に並ぶ（矢印）を選ぶ
                float distance = DistanceTo(frame, handle, ToVector(point), view);
                if (distance <= bestDistance)
                {
                    best = handle;
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    private static float DistanceTo(GizmoFrame frame, GizmoHandle handle, Vector2 point, ViewInfo view)
    {
        float best = float.MaxValue;
        foreach (var (a, b) in GizmoMath.Segments(frame, handle).Take(handle.Mode == GizmoMode.Move ? 1 : GizmoMath.RingSegments))
        {
            if (Picking.TryProject(a, view.ViewProjection, view.Size, out Vector2 sa) && Picking.TryProject(b, view.ViewProjection, view.Size, out Vector2 sb))
            {
                best = Math.Min(best, Picking.DistanceToSegment(point, sa, sb));
            }
        }
        return best;
    }

    private static Vector2 ToVector(System.Windows.Point p) => new((float)p.X, (float)p.Y);
}
