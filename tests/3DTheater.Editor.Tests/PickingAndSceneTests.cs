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
public sealed class SceneTests
{
    [TestMethod]
    public void DefaultSceneHasExpectedGroupsAndTestObject()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        CollectionAssert.AreEqual(new[] { "カメラ", "照明", "モデル", "ステージ" }, scene.Root.Children.Select(c => c.Name).ToArray());
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
