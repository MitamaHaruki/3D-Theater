using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>補間曲線の編集と、グラフエディタに描く値。</summary>
[TestClass]
public sealed class CurveEditingTests
{
    private static readonly BezierCurve Ease = BezierCurve.FromMmd(64, 0, 64, 127);

    private static BoneKey BoneAt(int frame, float x, float degreesY = 0) =>
        new(frame, new Vector3(x, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, degreesY * MathF.PI / 180), BoneInterpolation.Linear);

    [TestMethod]
    public void OneChannelOrAllChannelsCanBeChanged()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        BoneNode bone = model.Bones[0];
        bone.Keys.SetRange([BoneAt(0, 0), BoneAt(30, 10) with { Kind = KeyInterpolationKind.Step }]);
        var key = new KeyRef(bone, 30);

        Assert.AreEqual(1, CurveEditing.SetCurve([key], 3, Ease));

        Assert.IsTrue(CurveEditing.TryGetCurve(key, 3, out BezierCurve rotation, out KeyInterpolationKind kind));
        Assert.AreEqual(Ease, rotation, "回転の曲線だけが変わる");
        Assert.AreEqual(KeyInterpolationKind.Curve, kind, "段階のキーは曲線に戻る");
        CurveEditing.TryGetCurve(key, 0, out BezierCurve x, out _);
        Assert.AreEqual(BezierCurve.Linear, x);

        CurveEditing.SetCurve([key], null, Ease);
        CurveEditing.TryGetCurve(key, 0, out x, out _);
        Assert.AreEqual(Ease, x, "null なら全部の曲線");
        Assert.AreEqual(0, CurveEditing.SetCurve([new KeyRef(bone, 15)], null, Ease), "キーの無いフレームは変えない");
    }

    [TestMethod]
    public void MorphAndCameraCurvesAreEditedToo()
    {
        var (scene, _, _, model) = AnimationFixtures.Create();
        MorphNode morph = model.Morphs[0];
        morph.Keys.SetRange([new MorphKey(0, 0, BezierCurve.Linear), new MorphKey(10, 1, BezierCurve.Linear)]);
        CameraNode camera = scene.FindFirst<CameraNode>()!;
        camera.Keys.SetRange([new CameraKey(0, Vector3.Zero, Vector3.Zero, 45, 30, CameraInterpolation.Linear),
            new CameraKey(20, Vector3.One, Vector3.Zero, 30, 30, CameraInterpolation.Linear)]);

        CurveEditing.SetCurve([new KeyRef(morph, 10), new KeyRef(camera, 20)], 4, Ease);

        Assert.AreEqual(BezierCurve.Linear, morph.Keys.Keys[1].Curve, "モーフの曲線は 1 本だけ（番号 0）");
        Assert.AreEqual(Ease, camera.Keys.Keys[1].Interpolation.Distance, "カメラの距離の曲線");
        Assert.AreEqual(BezierCurve.Linear, camera.Keys.Keys[1].Interpolation.X);
        CurveEditing.SetCurve([new KeyRef(morph, 10)], 0, Ease);
        Assert.AreEqual(Ease, morph.Keys.Keys[1].Curve);
        CollectionAssert.AreEqual(new[] { "値" }, CurveEditing.ChannelNames(morph).ToArray());
        Assert.HasCount(6, CurveEditing.ChannelNames(camera));
    }

    [TestMethod]
    public void PreviousKeyIsWhereTheCurveStarts()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        BoneNode bone = model.Bones[0];
        bone.Keys.SetRange([BoneAt(0, 0), BoneAt(12, 1), BoneAt(30, 2)]);

        Assert.AreEqual(12, CurveEditing.PreviousKeyFrame(new KeyRef(bone, 30)));
        Assert.IsNull(CurveEditing.PreviousKeyFrame(new KeyRef(bone, 0)), "最初のキーには前が無い");
        CollectionAssert.AreEqual(new[] { 0, 12, 30 }, CurveEditing.KeyFrames(bone).ToArray());
    }

    [TestMethod]
    public void SampledValuesFollowTheCurveAndKeepRotationContinuous()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        BoneNode bone = model.Bones[0];
        bone.Keys.SetRange([BoneAt(0, 0, 0), BoneAt(10, 10, 170) with { Interpolation = new BoneInterpolation(Ease, Ease, Ease, BezierCurve.Linear) },
            BoneAt(20, 10, 340)]);

        IReadOnlyList<GraphSeries> series = CurveEditing.Sample(bone, 0, 20);

        GraphSeries x = series.Single(s => s.Name == "移動 X");
        Assert.HasCount(21, x.Values);
        Assert.AreEqual(5f, x.Values[5], 0.1f, "なめらかな曲線の真ん中はほぼ半分（64/127 は 0.5 より少し大きいので、ぴったりではない）");
        Assert.IsLessThan(2.5f, x.Values[2], "ゆっくり動き出す");
        GraphSeries rotationY = series.Single(s => s.Name == "回転 Y");
        Assert.AreEqual(3, rotationY.CurveChannel, "回転の線は回転の曲線で動く");
        for (int i = 1; i < rotationY.Values.Length; i++)
        {
            Assert.IsLessThan(30f, MathF.Abs(rotationY.Values[i] - rotationY.Values[i - 1]), "180° を越えても飛ばずにつながる");
        }
        Assert.AreEqual(0, playback.CurrentFrame, "シーンの再生位置は動かさない");
    }

    [TestMethod]
    public void MmdRoundingMatchesTheFileFormat()
    {
        BezierCurve rough = new(new Vector2(0.3337f, 0.1f), new Vector2(0.9f, 1.2f));

        var (x1, y1, x2, y2) = CurveEditing.ToMmd(CurveEditing.RoundToMmd(rough));

        Assert.AreEqual((42, 13, 114, 127), (x1, y1, x2, y2), "0〜127 に丸め、範囲の外は端にそろえる");
    }
}
