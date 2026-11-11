using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class PickingTests
{
    [TestMethod]
    public void RayThroughScreenCenterHitsCubeAtTarget()
    {
        var camera = new ViewCamera(ViewKind.Free);
        ViewMatrices m = camera.GetMatrices(4f / 3f, null);
        Ray ray = Picking.CreateRay(new Vector2(400f, 300f), new Vector2(800f, 600f), m.View, m.Projection);

        float? hit = Picking.IntersectUnitCube(ray, Matrix4x4.CreateTranslation(camera.Target));
        Assert.IsNotNull(hit);
        Assert.AreEqual(camera.Distance - 0.5f, hit.Value, 0.6f, "手前の面に当たる");
    }

    [TestMethod]
    public void RayAtCornerMissesCube()
    {
        var camera = new ViewCamera(ViewKind.Front);
        ViewMatrices m = camera.GetMatrices(1f, null);
        Ray ray = Picking.CreateRay(new Vector2(5f, 5f), new Vector2(600f, 600f), m.View, m.Projection);
        Assert.IsNull(Picking.IntersectUnitCube(ray, Matrix4x4.CreateTranslation(camera.Target)));
    }

    [TestMethod]
    public void ScaledAndRotatedCubeIsHitInsideItsBounds()
    {
        Matrix4x4 world = Matrix4x4.CreateScale(4f, 1f, 1f) * Matrix4x4.CreateRotationY(MathF.PI / 2f);
        // X 方向に 4 倍した箱を 90 度回すと、Z 方向に長くなる
        var alongZ = new Ray(new Vector3(0f, 0f, -10f), Vector3.UnitZ);
        var offsetX = new Ray(new Vector3(1.5f, 0f, -10f), Vector3.UnitZ);
        var offsetZ = new Ray(new Vector3(-10f, 0f, 1.5f), Vector3.UnitX);
        Assert.IsNotNull(Picking.IntersectUnitCube(alongZ, world));
        Assert.IsNull(Picking.IntersectUnitCube(offsetX, world), "X 方向には 1 しかない");
        Assert.IsNotNull(Picking.IntersectUnitCube(offsetZ, world), "Z 方向には 4 ある");
    }
}

[TestClass]
public sealed class MeshPickingTests
{
    // XY 平面の 1 辺 2 の四角形
    private static readonly Vector3[] Quad = [new(-1, 1, 0), new(1, 1, 0), new(1, -1, 0), new(-1, -1, 0)];
    private static readonly uint[] QuadIndices = [0, 1, 2, 0, 2, 3];
    private static readonly Vector3 Min = new(-1, -1, 0);
    private static readonly Vector3 Max = new(1, 1, 0);

    [TestMethod]
    public void HitsFromFrontAndBackAndReportsWorldDistance()
    {
        var front = new Ray(new Vector3(0.5f, 0.5f, -5), Vector3.UnitZ);
        Assert.AreEqual(5f, Picking.IntersectTriangles(front, Matrix4x4.Identity, Quad, QuadIndices, Min, Max)!.Value, 1e-4f);

        var back = new Ray(new Vector3(0, 0, 3), -Vector3.UnitZ);
        Assert.AreEqual(3f, Picking.IntersectTriangles(back, Matrix4x4.Identity, Quad, QuadIndices, Min, Max)!.Value, 1e-4f, "裏からでも当たる");

        Matrix4x4 moved = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0, 0, 2);
        Assert.AreEqual(7f, Picking.IntersectTriangles(front, moved, Quad, QuadIndices, Min, Max)!.Value, 1e-4f, "置き方を反映した距離");
    }

    [TestMethod]
    public void MissesOutsideAndIgnoresBrokenTriangles()
    {
        var outside = new Ray(new Vector3(3, 0, -5), Vector3.UnitZ);
        Assert.IsNull(Picking.IntersectTriangles(outside, Matrix4x4.Identity, Quad, QuadIndices, Min, Max));

        var center = new Ray(new Vector3(0.5f, 0.5f, -5), Vector3.UnitZ);
        Assert.IsNull(Picking.IntersectTriangles(center, Matrix4x4.Identity, Quad, [0, 1, 99], Min, Max), "存在しない頂点を指す面は無視");
        Assert.IsNull(Picking.IntersectTriangles(center, Matrix4x4.Identity, [], [], Min, Max));
    }

    [TestMethod]
    public void ModelBoundingSphereFollowsTransform()
    {
        var model = new ModelNode("人", "a.pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, 0, 0, 0, 0), "",
            new Vector3(-2, 0, -1), new Vector3(2, 20, 1)) { Position = new Vector3(5, 0, 0), Scale = new Vector3(2) };
        var (center, radius) = SceneEvaluator.BoundingSphere(model);
        Assert.AreEqual(new Vector3(5, 20, 0), center, "箱の中心 (0, 10, 0) を 2 倍して動かした位置");
        Assert.AreEqual(new Vector3(4, 20, 2).Length(), radius, 1e-4f);
    }
}

[TestClass]
public sealed class SceneTests
{
    [TestMethod]
    public void DefaultSceneHasExpectedGroupsAndTestObject()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        CollectionAssert.AreEqual(new[] { "カメラ", "照明", "映像効果", "セルフ影", "モデル", "ステージ", "音声" }, scene.Root.Children.Select(c => c.Name).ToArray());
        Assert.AreSame(scene.Effects, scene.FixedNodes["effects"], "映像効果は決まった項目");
        Assert.AreSame(scene.SelfShadow, scene.FixedNodes["selfShadow"], "セルフ影も決まった項目");
        Assert.IsNotNull(scene.FindFirst<CameraNode>());
        Assert.IsNotNull(scene.FindFirst<LightNode>());
        Assert.IsNotNull(scene.FindFirst<TestObjectNode>());
        Assert.IsFalse(scene.Root.Children[0].CanRename, "グループの名前は固定");
    }

    [TestMethod]
    public void NodeChangesAreReportedByDocument()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var changes = new List<string?>();
        scene.NodeChanged += (_, e) => changes.Add(e.PropertyName);
        TestObjectNode cube = scene.FindFirst<TestObjectNode>()!;
        cube.Position = new Vector3(1f, 2f, 3f);
        cube.IsVisible = false;
        CollectionAssert.AreEqual(new[] { nameof(SceneNode.Position), nameof(SceneNode.IsVisible) }, changes);
    }

    [TestMethod]
    public void VisibilityAndLockFollowParents()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        TestObjectNode cube = scene.FindFirst<TestObjectNode>()!;
        SceneNode models = cube.Parent!;

        models.IsVisible = false;
        Assert.IsTrue(cube.IsVisible);
        Assert.IsFalse(cube.IsEffectivelyVisible, "親が非表示なら見えない");

        models.IsLocked = true;
        Assert.IsFalse(cube.CanEdit, "親がロックされていれば編集できない");
    }

    [TestMethod]
    public void NamesMustNotBeEmpty()
    {
        var node = new SceneNode(SceneNodeKind.TestObject, "立方体");
        Assert.ThrowsExactly<ArgumentException>(() => node.Name = "   ");
        node.Name = "  新しい名前  ";
        Assert.AreEqual("新しい名前", node.Name, "前後の空白は取り除く");
    }

    [TestMethod]
    public void TestObjectSpinsWithFrame()
    {
        var cube = new TestObjectNode("立方体");
        Matrix4x4 atZero = SceneEvaluator.TestObjectWorld(cube, 0);
        Matrix4x4 at30 = SceneEvaluator.TestObjectWorld(cube, 30);
        Assert.AreNotEqual(atZero, at30);

        cube.SpinWithFrame = false;
        Assert.AreEqual(SceneEvaluator.TestObjectWorld(cube, 0), SceneEvaluator.TestObjectWorld(cube, 30));
    }

    [TestMethod]
    public void PreviewLayoutsProvideRequiredPresets()
    {
        Assert.AreEqual(1, PreviewLayout.Single.ViewCount());
        Assert.AreEqual(2, PreviewLayout.SideBySide.ViewCount());
        Assert.AreEqual(4, PreviewLayout.Quad.ViewCount());
        // 指示書 2.4 の例
        Assert.IsTrue(PreviewLayouts.QuadPresets.Any(p => p.Views.SequenceEqual([ViewKind.Front, ViewKind.Right, ViewKind.Back, ViewKind.Free])));
        Assert.IsTrue(PreviewLayouts.QuadPresets.Any(p => p.Views.SequenceEqual([ViewKind.Front, ViewKind.Right, ViewKind.Top, ViewKind.Free])));
        Assert.HasCount(4, PreviewLayouts.DefaultViews(PreviewLayout.Quad));
    }
}
