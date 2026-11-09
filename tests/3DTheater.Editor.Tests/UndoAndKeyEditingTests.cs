using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class UndoTests
{
    private static (ModelNode Model, PlaybackService Playback, AnimationService Animation, EditService Edits) Create()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        return (model, playback, animation, new EditService(new UndoService(), animation, playback));
    }

    [TestMethod]
    public void ValueEditsCanBeUndoneAndRedone()
    {
        var (model, _, _, edits) = Create();
        BoneNode center = model.Bones[0];

        edits.Change("センターの移動", [center], () => center.Position = new Vector3(0, 2, 0));
        Assert.IsTrue(edits.Undo.CanUndo);
        Assert.AreEqual("センターの移動", edits.Undo.UndoDescription);

        edits.Undo.Undo();
        Assert.AreEqual(Vector3.Zero, center.Position);
        Assert.IsFalse(center.HasUnregisteredChange, "元に戻すと未登録の印も戻る");

        edits.Undo.Redo();
        Assert.AreEqual(new Vector3(0, 2, 0), center.Position);
        Assert.IsTrue(center.HasUnregisteredChange);
    }

    [TestMethod]
    public void NothingIsRecordedWhenNothingChanges()
    {
        var (model, _, _, edits) = Create();
        Assert.IsFalse(edits.Change("何もしない", [model.Bones[0]], () => model.Bones[0].Position = Vector3.Zero));
        Assert.IsFalse(edits.Undo.CanUndo);
    }

    [TestMethod]
    public void ContinuousChangesOfTheSameValueAreMerged()
    {
        var (model, _, _, edits) = Create();
        MorphNode blink = model.Morphs[0];
        for (int i = 1; i <= 10; i++)
        {
            edits.Change("まばたきの値", [blink], () => blink.Weight = i / 10f, mergeKey: "blink");
        }
        edits.Change("あの値", [model.Morphs[1]], () => model.Morphs[1].Weight = 0.5f, mergeKey: "a");

        Assert.AreEqual(2, edits.Undo.UndoCount, "つまみのドラッグは 1 つの操作にまとめる");
        edits.Undo.Undo();
        edits.Undo.Undo();
        Assert.AreEqual(0f, blink.Weight);
    }

    [TestMethod]
    public void NewEditClearsRedoAndUndoDoesNotMergeWithOlderAction()
    {
        var (model, _, _, edits) = Create();
        MorphNode blink = model.Morphs[0];
        edits.Change("1", [blink], () => blink.Weight = 0.3f, mergeKey: "blink");
        edits.Change("2", [model.Bones[0]], () => model.Bones[0].Position = Vector3.One);
        edits.Undo.Undo();
        Assert.IsTrue(edits.Undo.CanRedo);

        edits.Change("3", [blink], () => blink.Weight = 0.6f, mergeKey: "blink");

        Assert.IsFalse(edits.Undo.CanRedo, "新しい操作をするとやり直しは消える");
        Assert.AreEqual(2, edits.Undo.UndoCount, "元に戻した後の操作は、前の操作とまとめない");
        edits.Undo.Undo();
        Assert.AreEqual(0.3f, blink.Weight);
    }

    [TestMethod]
    public void ChangesAreNotMergedAcrossAnAutoKeySwitch()
    {
        // 元に戻すの検証（でたらめな編集）で見つけた不具合: 自動キー ON の操作（キーも写す）に、OFF の操作（キーを写さない）を
        // まとめると、やり直しでキーが戻らなかった
        var (model, _, animation, edits) = Create();
        MorphNode blink = model.Morphs[0];
        animation.AutoKey = true;
        edits.Change("まばたきの値", [blink], () => blink.Weight = 0.4f, mergeKey: "blink"); // キーを置く
        animation.AutoKey = false;
        edits.Change("まばたきの値", [blink], () => blink.Weight = 0.7f, mergeKey: "blink");

        Assert.AreEqual(2, edits.Undo.UndoCount, "キーを写したかどうかが違う操作はまとめない");
        edits.Undo.Undo();
        edits.Undo.Undo();
        Assert.AreEqual(0, blink.Keys.Count);
        edits.Undo.Redo();
        Assert.AreEqual(0.4f, blink.Keys.Keys[0].Weight, "やり直すと、自動キーで置いたキーも戻る");
        edits.Undo.Redo();
        Assert.AreEqual(0.7f, blink.Weight);
        Assert.AreEqual(0.4f, blink.Keys.Keys[0].Weight);
    }

    [TestMethod]
    public void BoneRotationIsRestoredExactly()
    {
        // 元に戻すの検証で見つけた点: 写した回転を戻すとき、長さをそろえ直して値がわずかにずれていた
        var (model, _, _, edits) = Create();
        BoneNode center = model.Bones[0];
        for (int i = 0; i < 200; i++)
        {
            Quaternion q = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(i * 0.37f, i * 0.11f, i * -0.23f));
            center.Orientation = q;
            NodeState state = center.CaptureState();
            edits.Change("戻す", [center], () => center.Orientation = Quaternion.Identity);
            edits.Undo.Undo();
            Assert.AreEqual(state, center.CaptureState(), $"{i} 回目");
        }
    }

    [TestMethod]
    public void KeyRegistrationIsUndoneTogetherWithTheUnregisteredMark()
    {
        var (model, playback, animation, edits) = Create();
        BoneNode elbow = model.Bones[1];
        playback.Seek(10);
        edits.Change("左ひじの回転", [elbow], () => elbow.Rotation = new Vector3(0, 45, 0));

        edits.Change("キーの登録", [elbow], () => animation.Register([elbow]), includeKeys: true);
        Assert.AreEqual(1, elbow.Keys.Count);

        edits.Undo.Undo();
        Assert.AreEqual(0, elbow.Keys.Count);
        Assert.IsTrue(elbow.HasUnregisteredChange, "登録前の「未登録」に戻る");
        Assert.AreEqual(1, animation.UnregisteredCount);
        Assert.AreEqual(45f, elbow.Rotation.Y, 1e-3f);
    }

    [TestMethod]
    public void UndoReappliesKeysAtTheCurrentFrame()
    {
        var (model, playback, _, edits) = Create();
        BoneNode center = model.Bones[0];
        edits.Change("キーを置く", [center], () =>
        {
            center.Keys.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
            center.Keys.Set(new BoneKey(10, new Vector3(0, 10, 0), Quaternion.Identity, BoneInterpolation.Linear));
        }, includeKeys: true);
        edits.Change("キーを消す", [center], () => center.Keys.Remove(10), includeKeys: true);
        playback.Seek(5);
        Assert.AreEqual(Vector3.Zero, center.Position);

        edits.Undo.Undo(); // 消したキーが戻る

        Assert.AreEqual(new Vector3(0, 5, 0), center.Position, "戻した後、今のフレームの値に合わせ直す");
    }

    [TestMethod]
    public void HistoryIsLimited()
    {
        var (model, playback, animation, _) = Create();
        var edits = new EditService(new UndoService(limit: 3), animation, playback);
        for (int i = 0; i < 5; i++)
        {
            edits.Change($"{i}", [model.Bones[0]], () => model.Bones[0].Position = new Vector3(i + 1, 0, 0));
        }
        Assert.AreEqual(3, edits.Undo.UndoCount);
    }

    [TestMethod]
    public void NodeStatesRoundTripForEveryKind()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        CameraNode camera = scene.FindFirst<CameraNode>()!;
        NodeState before = camera.CaptureState();
        camera.Position = new Vector3(5, 5, 5);
        camera.FieldOfView = 90;
        camera.NearClip = 2;
        camera.FarClip = 3;
        camera.Name = "別のカメラ";

        camera.RestoreState(before);

        Assert.AreEqual(before, camera.CaptureState());
        Assert.AreEqual(1000f, camera.FarClip);

        LightNode light = scene.FindFirst<LightNode>()!;
        NodeState lightBefore = light.CaptureState();
        light.Intensity = 3;
        light.IsVisible = false;
        light.RestoreState(lightBefore);
        Assert.AreEqual(lightBefore, light.CaptureState());
    }
}

[TestClass]
public sealed class KeyEditingTests
{
    private static ModelNode ModelWithKeys()
    {
        var (_, _, _, model) = AnimationFixtures.Create();
        foreach (int f in new[] { 0, 10, 20 })
        {
            model.Bones[0].Keys.Set(new BoneKey(f, new Vector3(f, 0, 0), Quaternion.Identity, BoneInterpolation.Linear));
            model.Morphs[0].Keys.Set(new MorphKey(f, f / 20f, BezierCurve.Linear));
        }
        return model;
    }

    private static int[] Frames(BoneNode bone) => [.. bone.Keys.Keys.Select(k => k.Frame)];

    [TestMethod]
    public void MovingKeysOverwritesUnselectedKeysAtTheDestination()
    {
        ModelNode model = ModelWithKeys();
        BoneNode center = model.Bones[0];

        IReadOnlyList<KeyRef> moved = KeyEditing.Move([new KeyRef(center, 10)], 10, duplicate: false);

        CollectionAssert.AreEqual(new[] { 0, 20 }, Frames(center));
        Assert.AreEqual(new Vector3(10, 0, 0), center.Keys.Keys[1].Translation, "20 のキーは動かしたキーで置き換わる");
        Assert.AreEqual(new KeyRef(center, 20), moved.Single());
    }

    [TestMethod]
    public void MovingSeveralKeysTogetherKeepsTheirOrder()
    {
        ModelNode model = ModelWithKeys();
        BoneNode center = model.Bones[0];

        KeyEditing.Move([new KeyRef(center, 10), new KeyRef(center, 20)], 5, duplicate: false);

        CollectionAssert.AreEqual(new[] { 0, 15, 25 }, Frames(center));
        Assert.AreEqual(new Vector3(20, 0, 0), center.Keys.Keys[2].Translation);
    }

    [TestMethod]
    public void DuplicatingKeepsTheOriginalAndFramesStayNonNegative()
    {
        ModelNode model = ModelWithKeys();
        BoneNode center = model.Bones[0];

        KeyEditing.Move([new KeyRef(center, 10)], 30, duplicate: true);
        CollectionAssert.AreEqual(new[] { 0, 10, 20, 40 }, Frames(center));

        Assert.AreEqual(-10, KeyEditing.ClampDelta([new KeyRef(center, 10), new KeyRef(center, 20)], -50));
        KeyEditing.Move([new KeyRef(center, 10)], -50, duplicate: false);
        Assert.AreEqual(0, center.Keys.Keys[0].Frame);
    }

    [TestMethod]
    public void SummaryAndRangeSelectionCollectKeysInsideGroups()
    {
        ModelNode model = ModelWithKeys();

        Assert.HasCount(2, KeyEditing.AtFrame(model, 10), "モデルの行の◆は、中のボーンとモーフのキーをまとめたもの");
        Assert.HasCount(4, KeyEditing.InRange([model], 5, 25));
        Assert.IsEmpty(KeyEditing.InRange([model.Bones[1]], 0, 100));
    }

    [TestMethod]
    public void DeleteCopyAndPaste()
    {
        ModelNode model = ModelWithKeys();
        BoneNode center = model.Bones[0];
        MorphNode blink = model.Morphs[0];

        KeyClipboard clip = KeyEditing.Copy([new KeyRef(center, 10), new KeyRef(center, 20), new KeyRef(blink, 20)]);
        Assert.AreEqual(3, clip.Count);
        IReadOnlyList<KeyRef> pasted = KeyEditing.Paste(clip, 100);
        CollectionAssert.AreEqual(new[] { 0, 10, 20, 100, 110 }, Frames(center));
        Assert.AreEqual(1f, blink.Keys.Keys[^1].Weight);
        Assert.AreEqual(110, blink.Keys.Keys[^1].Frame);
        Assert.HasCount(3, pasted);

        Assert.AreEqual(2, KeyEditing.Delete([new KeyRef(center, 0), new KeyRef(center, 100), new KeyRef(center, 55)]));
        CollectionAssert.AreEqual(new[] { 10, 20, 110 }, Frames(center));
    }

    [TestMethod]
    public void InterpolationPresetsChangeTheCurveAndKind()
    {
        ModelNode model = ModelWithKeys();
        BoneNode center = model.Bones[0];

        KeyEditing.SetInterpolation([new KeyRef(center, 10)], InterpolationPreset.Smooth);
        Assert.AreEqual(KeyEditing.SmoothCurve, center.Keys.Keys[1].Interpolation.Rotation);
        Assert.IsTrue(center.Keys.Keys[2].Interpolation.X.IsLinear, "選んでいないキーは変えない");

        KeyEditing.SetInterpolation([new KeyRef(model.Morphs[0], 20)], InterpolationPreset.Step);
        Assert.AreEqual(KeyInterpolationKind.Step, model.Morphs[0].Keys.Keys[2].Kind);
    }

    [TestMethod]
    public void SelectionDropsKeysThatNoLongerExist()
    {
        ModelNode model = ModelWithKeys();
        var selection = new KeySelectionService();
        selection.Set([new KeyRef(model.Bones[0], 10), new KeyRef(model.Bones[0], 20)]);

        KeyEditing.Delete([new KeyRef(model.Bones[0], 10)]);
        selection.Prune();

        Assert.AreEqual(1, selection.Count);
    }

    [TestMethod]
    public void KeyEditsAreUndoableThroughEditService()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        var edits = new EditService(new UndoService(), animation, playback);
        BoneNode center = model.Bones[0];
        center.Keys.Set(new BoneKey(10, Vector3.One, Quaternion.Identity, BoneInterpolation.Linear));

        edits.Change("キーの移動", [center], () => KeyEditing.Move([new KeyRef(center, 10)], 20, duplicate: false), includeKeys: true);
        CollectionAssert.AreEqual(new[] { 30 }, Frames(center));

        edits.Undo.Undo();
        CollectionAssert.AreEqual(new[] { 10 }, Frames(center));
    }
}
