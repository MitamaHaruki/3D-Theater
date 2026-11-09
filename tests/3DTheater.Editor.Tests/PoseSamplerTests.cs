using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>好きなフレームの姿勢を、シーンの値を書き換えずに求める（物理の焼き込み用）。</summary>
[TestClass]
public sealed class PoseSamplerTests
{
    private static BoneKey BoneAt(int frame, float y) => new(frame, new Vector3(0, y, 0), Quaternion.Identity, BoneInterpolation.Linear);

    private static (Vector3[] T, Quaternion[] R, float[] W) Sample(ModelNode model, float frame)
    {
        var t = new Vector3[model.Bones.Count];
        var r = new Quaternion[model.Bones.Count];
        var w = new float[model.Morphs.Count];
        PoseSampler.Sample(model, frame, t, r, w);
        return (t, r, w);
    }

    [TestMethod]
    public void KeyedValuesComeFromKeysAndOthersFromTheNodes()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        BoneNode center = model.Bones[0];
        BoneNode elbow = model.Bones[1];
        center.Keys.SetRange([BoneAt(0, 0), BoneAt(10, 10)]);
        model.Morphs[0].Keys.SetRange([new MorphKey(0, 0, BezierCurve.Linear), new MorphKey(10, 1, BezierCurve.Linear)]);
        elbow.Position = new Vector3(0, 0, 2); // キーの無いボーン: 今の値をどのフレームでも使う
        model.Morphs[1].Weight = 0.25f;

        var (t, _, w) = Sample(model, 5);

        Assert.AreEqual(5f, t[0].Y, 1e-4f, "キーの値");
        Assert.AreEqual(new Vector3(0, 0, 2), t[1], "キーの無いボーンは今の値");
        Assert.AreEqual(0.5f, w[0], 1e-4f);
        Assert.AreEqual(0.25f, w[1]);
        Assert.AreEqual(0, playback.CurrentFrame, "シーンの再生位置は動かさない");
        Assert.AreEqual(0f, center.Position.Y, "シーンの値も書き換えない");
    }

    [TestMethod]
    public void UnregisteredChangesWinAtEveryFrame()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        BoneNode center = model.Bones[0];
        center.Keys.SetRange([BoneAt(0, 0), BoneAt(10, 10)]);
        center.Position = new Vector3(0, 3, 0); // キーのあるボーンを手で動かす → 未登録の変更

        Assert.IsTrue(center.HasUnregisteredChange);
        Assert.AreEqual(3f, Sample(model, 8).T[0].Y, "未登録の変更は、フレームを動かしても保つ（AnimationService と同じ）");
        playback.Seek(8);
        Assert.AreEqual(3f, center.Position.Y);
    }

    [TestMethod]
    public void MatchesTheSceneAtTheCurrentFrame()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        model.Bones[0].Keys.SetRange([BoneAt(0, 0), BoneAt(30, 6)]);
        model.Bones[1].Keys.SetRange([new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear),
            new BoneKey(30, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), BoneInterpolation.Linear)]);
        playback.Seek(12);

        var (t, r, _) = Sample(model, 12);

        Assert.AreEqual(model.Bones[0].Position, t[0]);
        Assert.AreEqual(model.Bones[1].Orientation, r[1]);
    }

    [TestMethod]
    public void OnlyChangesThatAffectOtherFramesAreReported()
    {
        var (scene, playback, _, model) = AnimationFixtures.Create();
        BoneNode keyed = model.Bones[0];
        BoneNode plain = model.Bones[1];
        keyed.Keys.SetRange([BoneAt(0, 0), BoneAt(10, 10)]);
        var affected = new List<ModelNode?>();
        scene.NodeChanged += (_, e) => affected.Add(PoseSampler.ModelAffectedBy(e));

        playback.Seek(5); // キーの当てはめ（キーから決まる値）
        Assert.IsTrue(affected.All(m => m is null), "再生位置を動かしただけでは、焼き込みをやり直さない");

        affected.Clear();
        plain.Position = Vector3.One; // キーの無いボーン
        Assert.Contains(model, affected);

        affected.Clear();
        keyed.Position = Vector3.One; // キーのあるボーンを手で動かす → 未登録の印が付く
        Assert.Contains(model, affected);

        affected.Clear();
        model.Rotation = new Vector3(0, 0, 30); // モデルを傾けると重力の向きが変わる
        Assert.Contains(model, affected);
    }
}
