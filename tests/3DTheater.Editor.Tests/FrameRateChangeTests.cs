using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>作品のフレームの速さを変えたときのキーの扱い（位置を合わせる・番号のまま）と、元に戻せること。</summary>
[TestClass]
public sealed class FrameRateChangeTests
{
    private static (SceneDocument Scene, PlaybackService Playback, UndoService Undo, EditService Edits, BoneNode Bone, CameraNode Camera) Create()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        var undo = new UndoService();
        return (scene, playback, undo, new EditService(undo, animation, playback), model.Bones[0], scene.FindFirst<CameraNode>()!);
    }

    private static BoneKey BoneAt(int frame, float y) => new(frame, new Vector3(0, y, 0), Quaternion.Identity, BoneInterpolation.Linear);

    private static CameraKey CameraAt(int frame, float distance) => new(frame, Vector3.Zero, Vector3.Zero, distance, 30, CameraInterpolation.Linear);

    private static SceneNode[] Keyed(SceneDocument scene) => [.. scene.AllNodes.Where(n => KeyEditing.CountKeys(n) > 0)];

    private static int[] Frames<TKey>(KeyTrack<TKey> track) where TKey : struct, IKeyframe => [.. track.Keys.Select(k => k.Frame)];

    [TestMethod]
    public void RescalingKeepsTheTimingAndCanBeUndone()
    {
        var (scene, playback, undo, edits, bone, camera) = Create();
        bone.Keys.SetRange([BoneAt(0, 0), BoneAt(30, 2)]);
        camera.Keys.SetRange([CameraAt(10, 45), CameraAt(11, 20), CameraAt(40, 20)]); // 10 → 11 はカメラの切り替え
        playback.EndFrame = 90;
        playback.Seek(15);

        int lost = edits.ChangeFrameRate(60, Keyed(scene), rescaleKeys: true);

        Assert.AreEqual(0, lost);
        Assert.AreEqual(60, playback.FrameRate);
        Assert.AreEqual(180, playback.EndFrame, "終わりのフレームも合わせる（秒数を保つ）");
        Assert.AreEqual(30, playback.CurrentFrame, "今の位置も同じ時刻のフレームへ");
        CollectionAssert.AreEqual(new[] { 0, 60 }, Frames(bone.Keys));
        CollectionAssert.AreEqual(new[] { 20, 22, 80 }, Frames(camera.Keys));
        Assert.AreEqual(KeyInterpolationKind.Step, camera.Keys.Keys[1].Kind, "切り替えの間に途中の絵を出さない");
        Assert.AreEqual(1f, bone.Position.Y, 1e-4f, "同じ時刻なら同じ姿勢（0.5 秒 = 30 フレーム目）");

        Assert.IsTrue(undo.Undo());
        Assert.AreEqual(30, playback.FrameRate);
        Assert.AreEqual(90, playback.EndFrame);
        Assert.AreEqual(15, playback.CurrentFrame);
        CollectionAssert.AreEqual(new[] { 0, 30 }, Frames(bone.Keys));
        CollectionAssert.AreEqual(new[] { 10, 11, 40 }, Frames(camera.Keys));
        Assert.AreEqual(KeyInterpolationKind.Curve, camera.Keys.Keys[1].Kind);

        Assert.IsTrue(undo.Redo());
        Assert.AreEqual(60, playback.FrameRate);
        CollectionAssert.AreEqual(new[] { 0, 60 }, Frames(bone.Keys));
    }

    [TestMethod]
    public void KeepingFrameNumbersOnlyChangesTheRate()
    {
        var (scene, playback, undo, edits, bone, _) = Create();
        bone.Keys.SetRange([BoneAt(0, 0), BoneAt(30, 2)]);
        playback.Seek(12);

        edits.ChangeFrameRate(24, Keyed(scene), rescaleKeys: false);

        Assert.AreEqual(24, playback.FrameRate);
        Assert.AreEqual(300, playback.EndFrame);
        Assert.AreEqual(12, playback.CurrentFrame);
        CollectionAssert.AreEqual(new[] { 0, 30 }, Frames(bone.Keys));
        StringAssert.Contains(undo.UndoDescription, "24 fps");

        undo.Undo();
        Assert.AreEqual(30, playback.FrameRate);
    }

    [TestMethod]
    public void ShrinkingMergesKeysThatLandOnTheSameFrame()
    {
        var (scene, playback, undo, edits, _, _) = Create();
        playback.FrameRate = 60;
        MorphNode morph = scene.AllNodes.OfType<MorphNode>().First();
        morph.Keys.SetRange([new MorphKey(0, 0, BezierCurve.Linear), new MorphKey(1, 0.5f, BezierCurve.Linear),
            new MorphKey(2, 1, BezierCurve.Linear), new MorphKey(10, 0, BezierCurve.Linear)]);

        int lost = edits.ChangeFrameRate(30, Keyed(scene), rescaleKeys: true);

        Assert.AreEqual(1, lost, "1 と 2 はどちらも 1 フレーム目になる（0.5 は 1 へ丸める）");
        CollectionAssert.AreEqual(new[] { 0, 1, 5 }, Frames(morph.Keys));
        Assert.AreEqual(1f, morph.Keys.Keys[1].Weight, "重なったら後ろのキーを使う");

        undo.Undo();
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 10 }, Frames(morph.Keys));
    }

    [TestMethod]
    public void UnsupportedOrSameRateDoesNothing()
    {
        var (scene, playback, undo, edits, _, _) = Create();

        edits.ChangeFrameRate(30, Keyed(scene), rescaleKeys: true);
        edits.ChangeFrameRate(50, Keyed(scene), rescaleKeys: true);

        Assert.AreEqual(30, playback.FrameRate);
        Assert.IsFalse(undo.CanUndo);
    }
}
