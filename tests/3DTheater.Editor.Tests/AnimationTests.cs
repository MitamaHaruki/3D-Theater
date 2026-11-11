using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

internal static class AnimationFixtures
{
    public static readonly BoneTraits Movable = new(true, true, false, false, false, false, false, true, string.Empty);

    /// <summary>ボーン「センター」「左ひじ」、モーフ「まばたき」「あ」、材質「肌」「髪」「服」を持つモデルをシーンに置く。</summary>
    public static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, ModelNode Model) Create()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var model = new ModelNode("人形", "人形.pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, 2, 2, 0, 0), string.Empty, Vector3.Zero, Vector3.One)
        {
            MaterialNames = ["肌", "髪", "服"],
        };
        BoneNode[] bones = [new BoneNode("センター", 0, Movable), new BoneNode("左ひじ", 1, Movable)];
        MorphNode[] morphs = [new MorphNode("まばたき", 0, MorphCategory.Eye, "頂点"), new MorphNode("あ", 1, MorphCategory.Mouth, "頂点")];
        var boneGroup = new SceneNode(SceneNodeKind.Group, "ボーン", canRename: false, hasTransform: false);
        var morphGroup = new SceneNode(SceneNodeKind.Group, "表情", canRename: false, hasTransform: false);
        foreach (BoneNode b in bones)
        {
            boneGroup.AddChild(b);
        }
        foreach (MorphNode m in morphs)
        {
            morphGroup.AddChild(m);
        }
        model.AttachParts([boneGroup, morphGroup], bones, morphs);
        scene.Add(scene.ModelsGroup, model);
        var playback = new PlaybackService();
        return (scene, playback, new AnimationService(scene, playback), model);
    }

    public static VmdName Name(string text, int bytes = VmdText.BoneNameBytes) => VmdText.Decode(VmdText.Encode(text, bytes));

    public static byte[] Interpolation(byte x1, byte y1, byte x2, byte y2)
    {
        var b = new byte[64];
        for (int ch = 0; ch < 4; ch++)
        {
            b[ch] = x1;
            b[ch + 4] = y1;
            b[ch + 8] = x2;
            b[ch + 12] = y2;
        }
        return b;
    }
}

[TestClass]
public sealed class KeyframeTests
{
    [TestMethod]
    public void LinearCurveIsIdentityAndEaseCurveBends()
    {
        Assert.IsTrue(BezierCurve.Linear.IsLinear);
        Assert.AreEqual(0.3f, BezierCurve.Linear.Evaluate(0.3f), 1e-6f);
        BezierCurve ease = BezierCurve.FromMmd(64, 0, 64, 127); // ゆっくり始まりゆっくり終わる
        Assert.IsLessThan(0.25f, ease.Evaluate(0.25f));
        Assert.AreEqual(0.5f, ease.Evaluate(0.5f), 0.01f);
        Assert.IsGreaterThan(0.75f, ease.Evaluate(0.75f));
        Assert.AreEqual(0f, ease.Evaluate(0f), 1e-6f);
        Assert.AreEqual(1f, ease.Evaluate(1f), 1e-5f);
    }

    [TestMethod]
    public void TrackKeepsKeysSortedAndReplacesSameFrame()
    {
        var track = new KeyTrack<MorphKey>();
        int changes = 0;
        track.Changed += (_, _) => changes++;
        track.Set(new MorphKey(30, 1, BezierCurve.Linear));
        track.Set(new MorphKey(0, 0, BezierCurve.Linear));
        track.Set(new MorphKey(30, 0.5f, BezierCurve.Linear));
        track.SetRange([new MorphKey(10, 0.2f, BezierCurve.Linear), new MorphKey(20, 0.4f, BezierCurve.Linear)]);

        CollectionAssert.AreEqual(new[] { 0, 10, 20, 30 }, track.Keys.Select(k => k.Frame).ToArray());
        Assert.AreEqual(0.5f, track.Keys[^1].Weight);
        Assert.AreEqual(4, changes, "まとめて置いたときの通知は 1 回");
        Assert.IsTrue(track.Remove(10));
        Assert.IsFalse(track.Remove(11));
        Assert.AreEqual((1, 3), track.Range(5, 30));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => track.Set(new MorphKey(-1, 0, BezierCurve.Linear)));
    }

    [TestMethod]
    public void BoneEvaluationInterpolatesAndHoldsEnds()
    {
        var track = new KeyTrack<BoneKey>();
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        track.Set(new BoneKey(10, new Vector3(0, 0, 0), Quaternion.Identity, BoneInterpolation.Linear));
        track.Set(new BoneKey(20, new Vector3(10, 0, 0), turned, BoneInterpolation.Linear));

        Assert.IsTrue(KeyframeEvaluator.TryEvaluate(track, 15, out Vector3 t, out Quaternion r));
        Assert.AreEqual(5f, t.X, 1e-5f);
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(r, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4))), 1e-5f);
        KeyframeEvaluator.TryEvaluate(track, 0, out t, out _);
        Assert.AreEqual(Vector3.Zero, t, "最初のキーより前は最初のキーの値");
        KeyframeEvaluator.TryEvaluate(track, 99, out t, out r);
        Assert.AreEqual(new Vector3(10, 0, 0), t, "最後のキーより後ろは最後のキーの値");
        Assert.AreEqual(turned, r);
        Assert.IsFalse(KeyframeEvaluator.TryEvaluate(new KeyTrack<BoneKey>(), 5, out _, out _));
    }

    [TestMethod]
    public void CurveAndStepChangeTheMotionBetweenKeys()
    {
        var track = new KeyTrack<BoneKey>();
        var ease = new BoneInterpolation(BezierCurve.FromMmd(127, 0, 127, 0), BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear);
        track.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        track.Set(new BoneKey(10, new Vector3(10, 10, 0), Quaternion.Identity, ease)); // 動き方は後ろのキーが持つ
        KeyframeEvaluator.TryEvaluate(track, 5, out Vector3 t, out _);
        Assert.IsLessThan(2f, t.X, "X だけゆっくり始まる");
        Assert.AreEqual(5f, t.Y, 1e-5f);

        var morph = new KeyTrack<MorphKey>();
        morph.Set(new MorphKey(0, 0, BezierCurve.Linear));
        morph.Set(new MorphKey(10, 1, BezierCurve.Linear, KeyInterpolationKind.Step));
        KeyframeEvaluator.TryEvaluate(morph, 9.9f, out float w);
        Assert.AreEqual(0f, w, "段階は次のキーまで値を変えない");
        KeyframeEvaluator.TryEvaluate(morph, 10, out w);
        Assert.AreEqual(1f, w);
    }

    [TestMethod]
    public void SceneRotationRoundTrips()
    {
        var random = new Random(3);
        for (int i = 0; i < 300; i++)
        {
            var degrees = new Vector3(random.Next(-89, 90), random.Next(-179, 180), random.Next(-179, 180));
            Quaternion q = SceneRotation.ToQuaternion(degrees);
            Vector3 back = SceneRotation.ToDegrees(q);
            Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(q, SceneRotation.ToQuaternion(back))), 1e-5f, $"{degrees} → {back}");
            Assert.AreEqual(degrees.X, back.X, 0.01f);
        }
    }

    [TestMethod]
    public void BoneOrientationAndDegreesStayInSync()
    {
        var bone = new BoneNode("頭", 0, AnimationFixtures.Movable);
        var changes = new List<string?>();
        bone.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        bone.Rotation = new Vector3(0, 90, 0);
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(bone.Orientation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2))), 1e-6f);
        bone.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
        Assert.AreEqual(30f, bone.Rotation.X, 1e-3f);
        bone.Orientation = bone.Orientation; // 同じ値なら知らせない

        CollectionAssert.AreEqual(new[] { nameof(SceneNode.Rotation), nameof(SceneNode.Rotation) }, changes);
    }
}

[TestClass]
public sealed class AnimationServiceTests
{
    [TestMethod]
    public void FrameChangesApplyKeysToBonesAndMorphs()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        model.Bones[0].Keys.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        model.Bones[0].Keys.Set(new BoneKey(10, new Vector3(0, 4, 0), Quaternion.Identity, BoneInterpolation.Linear));
        model.Morphs[0].Keys.Set(new MorphKey(10, 1, BezierCurve.Linear));

        playback.Seek(5);

        Assert.AreEqual(new Vector3(0, 2, 0), model.Bones[0].Position);
        Assert.AreEqual(1f, model.Morphs[0].Weight, "キーが 1 つならずっとその値");
        Assert.IsFalse(model.Bones[0].HasUnregisteredChange, "再生で変わった値は未登録の変更ではない");
    }

    [TestMethod]
    public void ManualEditsAreKeptAcrossFrameChangesUntilRegisteredOrDiscarded()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        BoneNode center = model.Bones[0];
        center.Keys.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        center.Keys.Set(new BoneKey(10, new Vector3(0, 4, 0), Quaternion.Identity, BoneInterpolation.Linear));
        int stateChanges = 0;
        animation.StateChanged += (_, _) => stateChanges++;

        center.Position = new Vector3(1, 0, 0);
        playback.Seek(10);

        Assert.IsTrue(center.HasUnregisteredChange);
        Assert.AreEqual(1, animation.UnregisteredCount);
        Assert.AreEqual(new Vector3(1, 0, 0), center.Position, "フレームを動かしても黙って捨てない");
        Assert.AreEqual(1, stateChanges);

        animation.DiscardUnregistered();
        Assert.AreEqual(new Vector3(0, 4, 0), center.Position, "捨てるとキーの値に戻る");
        Assert.AreEqual(0, animation.UnregisteredCount);
    }

    [TestMethod]
    public void RegisteringCreatesKeysAtCurrentFrame()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        playback.Seek(20);
        model.Bones[1].Rotation = new Vector3(0, 80, 0);
        model.Morphs[1].Weight = 0.5f;

        int registered = animation.RegisterUnregistered();

        Assert.AreEqual(2, registered);
        BoneKey key = model.Bones[1].Keys.Keys.Single();
        Assert.AreEqual(20, key.Frame);
        Assert.AreEqual(model.Bones[1].Orientation, key.Rotation);
        Assert.AreEqual(0.5f, model.Morphs[1].Keys.Keys.Single().Weight);
        Assert.AreEqual(0, animation.UnregisteredCount);
        Assert.IsFalse(model.Bones[1].HasUnregisteredChange);

        playback.Seek(0);
        Assert.AreEqual(80f, model.Bones[1].Rotation.Y, 1e-3f, "キーが 1 つならどのフレームでもその値");
    }

    [TestMethod]
    public void DiscardWithoutKeysReturnsToRestPose()
    {
        var (_, _, animation, model) = AnimationFixtures.Create();
        model.Bones[0].Position = new Vector3(3, 3, 3);
        model.Morphs[0].Weight = 1;

        animation.DiscardUnregistered();

        Assert.AreEqual(Vector3.Zero, model.Bones[0].Position);
        Assert.AreEqual(0f, model.Morphs[0].Weight);
    }

    [TestMethod]
    public void AutoKeyRegistersImmediately()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        animation.AutoKey = true;
        playback.Seek(7);

        model.Bones[0].Position = new Vector3(0, 1, 0);

        Assert.AreEqual(0, animation.UnregisteredCount);
        Assert.AreEqual(7, model.Bones[0].Keys.Keys.Single().Frame);
    }

    [TestMethod]
    public void RegistrationTargetsAreSelectedPartsAndPendingChangesInsideSelection()
    {
        var (scene, _, animation, model) = AnimationFixtures.Create();
        model.Morphs[0].Weight = 1; // 未登録

        Assert.HasCount(1, animation.RegistrationTargets([model]), "モデルを選んだときは未登録の変更だけ");
        CollectionAssert.AreEquivalent(new SceneNode[] { model.Bones[1], model.Morphs[0] }, animation.RegistrationTargets([model.Bones[1], model]).ToArray());
        Assert.HasCount(1, animation.RegistrationTargets([scene.FindFirst<CameraNode>()!]), "カメラを選んだときはカメラのキーを登録する");
        Assert.HasCount(1, animation.RegistrationTargets([scene.FindFirst<LightNode>()!]), "照明を選んだときは照明のキーを登録する");
        Assert.IsEmpty(animation.RegistrationTargets([scene.LightsGroup]), "照明のまとまりを選んだときは、未登録の変更だけ");
    }

    [TestMethod]
    public void LockedPartsAreNotRegistered()
    {
        var (_, _, animation, model) = AnimationFixtures.Create();
        model.Bones[0].Position = Vector3.One;
        model.Bones[0].IsLocked = true;

        Assert.AreEqual(0, animation.RegisterUnregistered());
        Assert.AreEqual(0, model.Bones[0].Keys.Count);
    }

    [TestMethod]
    public void ChangingKeysReappliesTheCurrentFrame()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        playback.Seek(3);
        model.Morphs[1].Keys.Set(new MorphKey(0, 0.25f, BezierCurve.Linear));

        Assert.AreEqual(0.25f, model.Morphs[1].Weight);
        Assert.AreEqual(0, AnimationService.LastKeyFrame(model));
    }
}

[TestClass]
public sealed class MotionImportTests
{
    [TestMethod]
    public void VmdKeysAreMatchedByNameAndConverted()
    {
        var (_, playback, _, model) = AnimationFixtures.Create();
        var vmd = new VmdDocument
        {
            ModelName = AnimationFixtures.Name("別のモデル", VmdText.ModelNameBytes),
            BoneKeys =
            [
                new VmdBoneKey(AnimationFixtures.Name("左ひじ"), 30, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1), AnimationFixtures.Interpolation(127, 0, 127, 0)),
                new VmdBoneKey(AnimationFixtures.Name("左ひじ"), 0, Vector3.Zero, Quaternion.Identity, AnimationFixtures.Interpolation(20, 20, 107, 107)),
                new VmdBoneKey(AnimationFixtures.Name("右ひじ"), 0, Vector3.Zero, Quaternion.Identity, AnimationFixtures.Interpolation(20, 20, 107, 107)),
            ],
            MorphKeys = [new VmdMorphKey(AnimationFixtures.Name("まばたき"), 15, 1), new VmdMorphKey(AnimationFixtures.Name("ウインク"), 0, 1)],
        };
        var report = new LoadReport();

        MotionImportResult result = MotionImport.ApplyVmd(vmd, model, frameRate: 30, report);

        Assert.AreEqual(2, result.BoneKeys);
        Assert.AreEqual(1, result.MorphKeys);
        Assert.AreEqual(30, result.LastFrame);
        BoneKey last = model.Bones[1].Keys.Keys[^1];
        Assert.AreEqual(30, last.Frame);
        Assert.IsFalse(last.Interpolation.Rotation.IsLinear);
        Assert.IsTrue(model.Bones[1].Keys.Keys[0].Interpolation.X.IsLinear);
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("右ひじ")), "モデルに無いボーンを知らせる");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("ウインク")));
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("別のモデル")));

        playback.Seek(15);
        Assert.AreEqual(1f, model.Morphs[0].Weight);
    }

    [TestMethod]
    public void FramesAreScaledToTheProjectFrameRate()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        var vmd = new VmdDocument
        {
            BoneKeys = [new VmdBoneKey(AnimationFixtures.Name("センター"), 15, Vector3.One, Quaternion.Identity, AnimationFixtures.Interpolation(20, 20, 107, 107))],
        };
        var report = new LoadReport();

        MotionImport.ApplyVmd(vmd, model, frameRate: 60, report);

        Assert.AreEqual(30, model.Bones[0].Keys.Keys.Single().Frame);
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("2 倍")));
    }

    [TestMethod]
    public void ImportMergesWithExistingKeys()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        model.Bones[0].Keys.Set(new BoneKey(100, Vector3.One, Quaternion.Identity, BoneInterpolation.Linear));
        model.Bones[0].Keys.Set(new BoneKey(0, Vector3.One, Quaternion.Identity, BoneInterpolation.Linear));
        var vmd = new VmdDocument
        {
            BoneKeys = [new VmdBoneKey(AnimationFixtures.Name("センター"), 0, Vector3.Zero, Quaternion.Identity, AnimationFixtures.Interpolation(20, 20, 107, 107))],
        };

        MotionImport.ApplyVmd(vmd, model, 30, new LoadReport());

        CollectionAssert.AreEqual(new[] { 0, 100 }, model.Bones[0].Keys.Keys.Select(k => k.Frame).ToArray());
        Assert.AreEqual(Vector3.Zero, model.Bones[0].Keys.Keys[0].Translation, "同じフレームは読み込んだキーで置き換える");
    }

    [TestMethod]
    public void MostlyMismatchedMotionIsAWarningAndCameraMotionIsExplained()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        var other = new VmdDocument
        {
            BoneKeys = [.. new[] { "a", "b", "c" }.Select(n => new VmdBoneKey(AnimationFixtures.Name(n), 0, Vector3.Zero, Quaternion.Identity, new byte[64]))],
        };
        var report = new LoadReport();
        MotionImport.ApplyVmd(other, model, 30, report);
        Assert.IsGreaterThanOrEqualTo(1, report.WarningCount);

        var camera = new VmdDocument { CameraKeys = [new VmdCameraKey(0, -45, Vector3.Zero, Vector3.Zero, new byte[24], 30, false)] };
        var cameraReport = new LoadReport();
        MotionImport.ApplyVmd(camera, model, 30, cameraReport);
        Assert.IsTrue(cameraReport.Issues.Any(i => i.Message.Contains("カメラ・照明のモーション")));
    }

    [TestMethod]
    public void VpdSetsCurrentValuesAsUnregisteredChanges()
    {
        var (_, _, animation, model) = AnimationFixtures.Create();
        model.Morphs[0].IsLocked = true;
        var vpd = new VpdDocument
        {
            Bones = [new VpdBone("左ひじ", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1)), new VpdBone("しっぽ", Vector3.Zero, Quaternion.Identity)],
            Morphs = [new VpdMorph("まばたき", 1), new VpdMorph("あ", 0.5f)],
        };
        var report = new LoadReport();

        PoseImportResult result = MotionImport.ApplyVpd(vpd, model, report);

        Assert.AreEqual(1, result.Bones);
        Assert.AreEqual(1, result.Morphs);
        Assert.AreEqual(0f, model.Morphs[0].Weight, "ロックしたモーフは変えない");
        Assert.AreEqual(0.5f, model.Morphs[1].Weight);
        Assert.AreEqual(2, animation.UnregisteredCount, "ポーズはキーにせず、未登録の変更にする");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("しっぽ")));
    }
}
