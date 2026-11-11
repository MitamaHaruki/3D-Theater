using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class PhysicsFloorSettingTests
{
    [TestMethod]
    public void FloorSettingDefaultsOnAndModelPlacementRebakesPhysics()
    {
        ProjectFile old = ProjectSerializer.Deserialize("""{"format":"3DTheater.Project","version":1}""", new LoadReport())!;
        Assert.IsTrue(old.PhysicsFloor, "床との当たりの無い前の版のファイルは、床に当てる");
        ProjectFile off = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new ProjectFile { PhysicsFloor = false }), new LoadReport())!;
        Assert.IsFalse(off.PhysicsFloor, "当てないことも保存する");

        var (_, _, _, model) = AnimationFixtures.Create();
        foreach (string property in new[] { nameof(SceneNode.Position), nameof(SceneNode.Rotation), nameof(SceneNode.Scale) })
        {
            Assert.AreSame(model, PoseSampler.ModelAffectedBy(new SceneNodeChangedEventArgs(model, property)),
                $"モデルの{property}が変わると、物理の床の場所が変わるので焼き込みをやり直す");
        }
        Assert.IsNull(PoseSampler.ModelAffectedBy(new SceneNodeChangedEventArgs(model, nameof(SceneNode.IsVisible))));
        _ = Vector3.Zero;
    }
}
