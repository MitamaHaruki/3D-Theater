using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class ModelPartsTests
{
    private static (SceneDocument Scene, ModelNode Model) CreateModelWithParts()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var summary = new ModelSummary("PMX 2.0", 0, 0, 0, 0, 2, 2, 0, 0);
        var model = new ModelNode("人形", "人形.pmx", summary, string.Empty, Vector3.Zero, Vector3.One);
        var traits = new BoneTraits(true, true, false, false, false, false, false, true, string.Empty);
        BoneNode[] bones = [new BoneNode("センター", 0, traits), new BoneNode("上半身", 1, traits with { ParentName = "センター" })];
        MorphNode[] morphs = [new MorphNode("まばたき", 0, MorphCategory.Eye, "頂点"), new MorphNode("あ", 1, MorphCategory.Mouth, "頂点")];
        var boneGroup = new SceneNode(SceneNodeKind.Group, "ボーン", canRename: false, hasTransform: false) { StartsCollapsed = true };
        var morphGroup = new SceneNode(SceneNodeKind.Group, "表情", canRename: false, hasTransform: false) { StartsCollapsed = true, CanToggleVisibility = false };
        foreach (BoneNode bone in bones)
        {
            boneGroup.AddChild(bone);
        }
        foreach (MorphNode morph in morphs)
        {
            morphGroup.AddChild(morph);
        }
        model.AttachParts([boneGroup, morphGroup], bones, morphs);
        scene.Add(scene.ModelsGroup, model);
        return (scene, model);
    }

    [TestMethod]
    public void BonesAndMorphsKnowTheirModelAndReportChanges()
    {
        var (scene, model) = CreateModelWithParts();
        var changes = new List<(SceneNode Node, string? Property)>();
        scene.NodeChanged += (_, e) => changes.Add((e.Node, e.PropertyName));

        model.Bones[1].Rotation = new Vector3(0, 30, 0);
        model.Morphs[0].Weight = 0.5f;

        Assert.AreSame(model, model.Bones[1].Model);
        Assert.AreSame(model, model.Morphs[0].Model);
        Assert.IsTrue(model.Bones[1].IsPosed);
        CollectionAssert.AreEqual(
            new (SceneNode, string?)[] { (model.Bones[1], nameof(SceneNode.Rotation)), (model.Morphs[0], nameof(MorphNode.Weight)) },
            changes);
    }

    [TestMethod]
    public void MorphWeightIsClampedToZeroToOne()
    {
        var morph = new MorphNode("ウィンク", 0, MorphCategory.Eye, "頂点");
        morph.Weight = 1.5f;
        Assert.AreEqual(1f, morph.Weight);
        morph.Weight = -2f;
        Assert.AreEqual(0f, morph.Weight);
        morph.Weight = float.NaN;
        Assert.AreEqual(0f, morph.Weight);
    }

    [TestMethod]
    public void BonesCannotBeRenamedOrScaled()
    {
        var (_, model) = CreateModelWithParts();
        BoneNode bone = model.Bones[0];
        Assert.IsFalse(bone.CanRename);
        Assert.IsFalse(bone.CanScale);
        Assert.IsTrue(bone.HasTransform);
        Assert.ThrowsExactly<InvalidOperationException>(() => bone.Name = "別の名前");
    }

    [TestMethod]
    public void ResetPoseSkipsLockedParts()
    {
        var (_, model) = CreateModelWithParts();
        model.Bones[0].Position = new Vector3(1, 2, 3);
        model.Bones[1].Rotation = new Vector3(10, 0, 0);
        model.Morphs[0].Weight = 1;
        model.Morphs[1].Weight = 1;
        model.Bones[1].IsLocked = true;
        model.Morphs[1].Parent!.IsLocked = true; // まとまり（「表情」）ごとのロック

        model.ResetPose();

        Assert.AreEqual(Vector3.Zero, model.Bones[0].Position);
        Assert.AreEqual(new Vector3(10, 0, 0), model.Bones[1].Rotation);
        Assert.AreEqual(1f, model.Morphs[0].Weight, "同じまとまりの中なのでロックされている");
        Assert.AreEqual(1f, model.Morphs[1].Weight);
    }

    [TestMethod]
    public void PartsCanBeAttachedOnlyOnceAndBeforeAddingToScene()
    {
        var (_, model) = CreateModelWithParts();
        Assert.ThrowsExactly<InvalidOperationException>(() => model.AttachParts([], [], []));

        var other = new ModelNode("別", "別.pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, 0, 0, 0, 0), string.Empty, Vector3.Zero, Vector3.One);
        var stray = new BoneNode("迷子", 0, new BoneTraits(true, false, false, false, false, false, false, true, string.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => other.AttachParts([], [stray], []));
    }

    [TestMethod]
    public void AncestorLookupFindsTheModel()
    {
        var (_, model) = CreateModelWithParts();
        Assert.AreSame(model, model.Morphs[1].FindAncestor<ModelNode>());
        Assert.IsNull(model.FindAncestor<ModelNode>());
    }
}

[TestClass]
public sealed class ScreenPickingTests
{
    [TestMethod]
    public void ProjectionMapsTheScreenCenterAndRejectsPointsBehind()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(new Vector3(0, 0, -10), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 4, 1, 0.1f, 100);
        var size = new Vector2(200, 200);

        Assert.IsTrue(Picking.TryProject(Vector3.Zero, view * projection, size, out Vector2 center));
        Assert.AreEqual(100f, center.X, 1e-3f);
        Assert.AreEqual(100f, center.Y, 1e-3f);
        Assert.IsTrue(Picking.TryProject(new Vector3(0, 1, 0), view * projection, size, out Vector2 above));
        Assert.IsLessThan(100f, above.Y, "上にある点は画面の上（Y が小さい）");
        Assert.IsFalse(Picking.TryProject(new Vector3(0, 0, -20), view * projection, size, out _));
    }

    [TestMethod]
    public void DistanceToSegmentUsesTheNearestPoint()
    {
        Assert.AreEqual(3f, Picking.DistanceToSegment(new Vector2(5, 3), Vector2.Zero, new Vector2(10, 0)), 1e-5f);
        Assert.AreEqual(5f, Picking.DistanceToSegment(new Vector2(13, 4), Vector2.Zero, new Vector2(10, 0)), 1e-5f);
        Assert.AreEqual(5f, Picking.DistanceToSegment(new Vector2(3, 4), Vector2.Zero, Vector2.Zero), 1e-5f);
    }

    [TestMethod]
    public void PickSegmentChoosesTheClosestWithinTolerance()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(new Vector3(0, 0, -10), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreateOrthographicLeftHanded(20, 20, 0.1f, 100);
        var size = new Vector2(200, 200); // 1 ピクセル = 0.1
        (Vector3, Vector3)[] segments =
        [
            (new Vector3(-5, 0, 0), new Vector3(5, 0, 0)),
            (new Vector3(-5, 1, 0), new Vector3(5, 1, 0)),
        ];

        Assert.AreEqual(0, Picking.PickSegment(new Vector2(100, 102), size, view * projection, segments, 6));
        Assert.AreEqual(1, Picking.PickSegment(new Vector2(100, 91), size, view * projection, segments, 6));
        Assert.AreEqual(-1, Picking.PickSegment(new Vector2(100, 150), size, view * projection, segments, 6));
    }
}
