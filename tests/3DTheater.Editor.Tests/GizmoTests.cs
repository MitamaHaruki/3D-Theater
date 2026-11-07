using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class GizmoTests
{
    // 正面（-Z から +Z を見る）の平行投影。画面は 200 × 200 ピクセルで、1 単位 = 10 ピクセル
    private static readonly Matrix4x4 View = Matrix4x4.CreateLookAtLeftHanded(new Vector3(0, 0, -50), Vector3.Zero, Vector3.UnitY);
    private static readonly Matrix4x4 Projection = Matrix4x4.CreateOrthographicLeftHanded(20, 20, 0.1f, 100);
    private static readonly Vector2 Size = new(200, 200);
    private static Matrix4x4 ViewProjection => View * Projection;

    private static Vector2 Screen(Vector3 world)
    {
        Picking.TryProject(world, ViewProjection, Size, out Vector2 p);
        return p;
    }

    private static Ray RayAt(Vector2 point) => Picking.CreateRay(point, Size, View, Projection);

    private static readonly GizmoFrame Frame = GizmoFrame.Create(Vector3.Zero, Quaternion.Identity, 5);

    private static readonly GizmoHandle[] AllHandles =
    [
        new(GizmoMode.Rotate, 0), new(GizmoMode.Rotate, 1), new(GizmoMode.Rotate, 2),
        new(GizmoMode.Move, 0), new(GizmoMode.Move, 1), new(GizmoMode.Move, 2),
    ];

    [TestMethod]
    public void SizeIsConstantOnScreen()
    {
        Assert.AreEqual(9f, GizmoMath.WorldSizeForPixels(Vector3.Zero, View, Projection, Size, 90), 1e-3f);
    }

    [TestMethod]
    public void HitTestFindsArrowsAndRings()
    {
        Assert.AreEqual(new GizmoHandle(GizmoMode.Move, 0), GizmoMath.HitTest(Frame, AllHandles, Screen(new Vector3(2.5f, 0, 0)), ViewProjection, Size, 6));
        Assert.AreEqual(new GizmoHandle(GizmoMode.Move, 1), GizmoMath.HitTest(Frame, AllHandles, Screen(new Vector3(0, 3, 0)) + new Vector2(3, 0), ViewProjection, Size, 6));
        float c = 5 * MathF.Cos(MathF.PI / 4);
        Assert.AreEqual(new GizmoHandle(GizmoMode.Rotate, 2), GizmoMath.HitTest(Frame, AllHandles, Screen(new Vector3(c, c, 0)), ViewProjection, Size, 6));
        Assert.IsNull(GizmoMath.HitTest(Frame, AllHandles, Screen(new Vector3(8, 8, 0)), ViewProjection, Size, 6));
    }

    [TestMethod]
    public void DraggingAnArrowMovesAlongItsAxis()
    {
        Vector2 start = Screen(new Vector3(2, 0, 0));
        Vector2 now = start + new Vector2(50, 20); // 横に 5 単位（縦の動きは軸と関係ない）

        float amount = GizmoMath.MoveAmount(Frame, 0, RayAt(start), RayAt(now), start, now, ViewProjection, Size);

        Assert.AreEqual(5f, amount, 1e-3f);
        Assert.AreEqual(0f, GizmoMath.MoveAmount(Frame, 2, RayAt(start), RayAt(now), start, now, ViewProjection, Size), 1e-3f,
            "こちらを向いた軸（Z）は、正面からは動かせない");
    }

    [TestMethod]
    public void DraggingARingRotatesAroundItsAxis()
    {
        Vector2 from = Screen(new Vector3(5, 0, 0));
        Vector2 to = Screen(new Vector3(0, 5, 0));

        float angle = GizmoMath.RotateAmount(Frame, 2, RayAt(from), RayAt(to), from, to, Vector2.Zero);

        Assert.AreEqual(MathF.PI / 2, angle, 1e-3f, "X から Y へは Z まわりに +90°");
    }

    [TestMethod]
    public void EdgeOnRingUsesTheTangentOnScreen()
    {
        // X まわりの輪は正面からは縦の線に見える。つかんだ所の接線に沿って動かすと回る
        Vector3 grab = GizmoMath.NearestRingPoint(Frame, 0, Screen(new Vector3(0, 5, 0)), ViewProjection, Size);
        Vector2 tangent = GizmoMath.RingTangent(Frame, 0, grab, ViewProjection, Size);
        Vector2 from = Screen(grab);

        float forward = GizmoMath.RotateAmount(Frame, 0, RayAt(from), RayAt(from + (Vector2.Normalize(tangent) * 100)), from, from + (Vector2.Normalize(tangent) * 100), tangent);
        float backward = GizmoMath.RotateAmount(Frame, 0, RayAt(from), RayAt(from - (Vector2.Normalize(tangent) * 100)), from, from - (Vector2.Normalize(tangent) * 100), tangent);

        Assert.AreEqual(MathF.PI / 2, MathF.Abs(forward), 1e-3f, "100 ピクセルで 90°");
        Assert.AreEqual(-forward, backward, 1e-5f, "逆に動かすと逆に回る");
        Assert.AreNotEqual(Vector2.Zero, tangent);
    }

    [TestMethod]
    public void BoneValuesAreConvertedToTheParentSpace()
    {
        Quaternion parent = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        Vector3 moved = GizmoMath.BoneTranslation(Vector3.Zero, Vector3.UnitX, parent, new Vector3(2));
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 0, 0.5f), moved), 1e-5f, "親が Y まわりに 90° 回っていると、ワールドの X は親の Z");

        Quaternion local = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        Quaternion rotated = GizmoMath.BoneRotation(local, Vector3.UnitX, 0.7f, parent);
        Quaternion expectedGlobal = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f) * parent * local;
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(parent * rotated, expectedGlobal)), 1e-5f, "ワールドの X まわりに回した向きになる");
    }

    [TestMethod]
    public void NodeRotationAndSnapping()
    {
        Vector3 degrees = GizmoMath.NodeRotation(Vector3.Zero, Vector3.UnitY, MathF.PI / 2);
        Assert.AreEqual(90f, degrees.Y, 1e-3f);
        Assert.AreEqual(1.5f, GizmoMath.Snap(1.37f, 0.5f));
        Assert.AreEqual(-5f, GizmoMath.Snap(-6.2f, 5f));
        Assert.AreEqual(1.37f, GizmoMath.Snap(1.37f, 0));
    }

    [TestMethod]
    public void StyleAndToolDecideWhichHandlesAreShown()
    {
        var settings = new GizmoSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        Assert.IsTrue(settings.Shows(GizmoMode.Move) && settings.Shows(GizmoMode.Rotate), "MMD 方式は移動と回転を一緒に出す");

        settings.Style = GizmoStyle.Blender;
        Assert.IsFalse(settings.Shows(GizmoMode.Move) || settings.Shows(GizmoMode.Rotate), "Blender 方式の選択の道具では出さない");
        settings.Tool = GizmoTool.Rotate;
        Assert.IsTrue(settings.Shows(GizmoMode.Rotate));
        Assert.IsFalse(settings.Shows(GizmoMode.Move));
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void DragIsOneUndoableActionAndCanBeCancelled()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        var edits = new EditService(new UndoService(), animation, playback);
        BoneNode elbow = model.Bones[1];

        EditTransaction drag = edits.Begin("左ひじの回転", [elbow]);
        for (int i = 1; i <= 20; i++)
        {
            elbow.Rotation = new Vector3(0, i * 3, 0);
        }
        Assert.IsTrue(drag.Commit());
        Assert.AreEqual(1, edits.Undo.UndoCount, "ドラッグ 1 回は 1 つの操作");
        edits.Undo.Undo();
        Assert.AreEqual(Quaternion.Identity, elbow.Orientation);
        Assert.IsFalse(elbow.HasUnregisteredChange);

        EditTransaction cancelled = edits.Begin("左ひじの回転", [elbow]);
        elbow.Rotation = new Vector3(0, 45, 0);
        cancelled.Cancel();
        Assert.AreEqual(Quaternion.Identity, elbow.Orientation, "取り消すと始める前に戻る");
        Assert.IsFalse(edits.Undo.CanUndo);
        Assert.IsFalse(cancelled.Commit(), "取り消した後は積めない");
    }
}
