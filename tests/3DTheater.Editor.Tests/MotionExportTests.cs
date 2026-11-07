using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class MotionExportTests
{
    [TestMethod]
    public void ExportedMotionImportsBackToTheSameKeys()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        var ease = new BoneInterpolation(BezierCurve.FromMmd(64, 0, 64, 127), BezierCurve.Linear, BezierCurve.FromMmd(10, 20, 30, 40), BezierCurve.Linear);
        model.Bones[1].Keys.SetRange(
        [
            new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear),
            new BoneKey(30, new Vector3(0, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.4f), ease),
        ]);
        model.Morphs[0].Keys.SetRange([new MorphKey(0, 0, BezierCurve.Linear), new MorphKey(10, 1, BezierCurve.Linear)]);

        var report = new LoadReport();
        byte[] bytes = VmdWriter.Write(MotionExport.CreateVmd(model, 30, report));
        Assert.IsFalse(report.Issues.Any(), string.Join("\n", report.Issues.Select(i => i.Message)));

        var (_, _, _, other) = AnimationFixtures.Create();
        MotionImport.ApplyVmd(VmdReader.Read(bytes).Document!, other, 30, new LoadReport());

        CollectionAssert.AreEqual(model.Bones[1].Keys.Keys.ToArray(), other.Bones[1].Keys.Keys.ToArray(), "曲線も含めて同じキーに戻る");
        CollectionAssert.AreEqual(model.Morphs[0].Keys.Keys.ToArray(), other.Morphs[0].Keys.Keys.ToArray());
        Assert.AreEqual(0, other.Bones[0].Keys.Count, "キーの無いボーンは書かない");
    }

    [TestMethod]
    public void FramesAreConvertedTo30FpsAndStepsBecomeAnExtraKey()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        model.Bones[0].Keys.SetRange(
        [
            new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear),
            new BoneKey(60, new Vector3(0, 5, 0), Quaternion.Identity, BoneInterpolation.Linear, KeyInterpolationKind.Step),
        ]);
        var report = new LoadReport();

        VmdDocument vmd = MotionExport.CreateVmd(model, frameRate: 60, report);

        CollectionAssert.AreEqual(new[] { 0, 29, 30 }, vmd.BoneKeys.Select(k => k.Frame).ToArray(), "60 fps の 60 フレーム目は 30 fps の 30 フレーム目");
        Assert.AreEqual(Vector3.Zero, vmd.BoneKeys[1].Translation, "段階の直前のフレームは前の値のまま");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("段階")));
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("0.5 倍")));
    }

    [TestMethod]
    public void UnregisteredChangesAreReportedAndPoseIsExported()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        model.Bones[1].Rotation = new Vector3(0, 30, 0); // 未登録
        model.Morphs[1].Weight = 0.5f;
        var report = new LoadReport();

        MotionExport.CreateVmd(model, 30, report);
        VpdDocument pose = MotionExport.CreateVpd(model, "人形.osm");

        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("登録していない")));
        Assert.AreEqual("左ひじ", pose.Bones.Single().Name);
        Assert.AreEqual(model.Bones[1].Orientation, pose.Bones[0].Rotation);
        Assert.AreEqual("あ", pose.Morphs.Single().Name);
    }
}
