using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>まとめた編集（プラグインの編集の土台。<see cref="EditService.Batch"/>）。</summary>
[TestClass]
public sealed class BatchEditTests
{
    private static (ModelNode Model, PlaybackService Playback, AnimationService Animation, EditService Edits) Create()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        return (model, playback, animation, new EditService(new UndoService(), animation, playback));
    }

    private static readonly Quaternion Bend = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);

    [TestMethod]
    public void ChangesToSeveralItemsBecomeOneUndoStep()
    {
        var (model, _, _, edits) = Create();
        BoneNode center = model.Bones[0];
        BoneNode elbow = model.Bones[1];
        MorphNode blink = model.Morphs[0];

        bool changed = edits.Batch("まばたきと姿勢", batch =>
        {
            Assert.IsTrue(batch.SetBonePose(center, new Vector3(0, 1, 0), Quaternion.Identity));
            Assert.IsTrue(batch.SetBoneKey(elbow, 10, Vector3.Zero, Bend));
            Assert.IsTrue(batch.SetMorphKey(blink, 0, 0f));
            Assert.IsTrue(batch.SetMorphKey(blink, 3, 1f));
            Assert.IsTrue(batch.SetMorphKey(blink, 6, 0f));
        });

        Assert.IsTrue(changed);
        Assert.AreEqual(1, edits.Undo.UndoCount, "まとめて 1 つの操作になる");
        Assert.AreEqual("まばたきと姿勢", edits.Undo.UndoDescription);
        Assert.AreEqual(3, blink.Keys.Count);
        Assert.AreEqual(1, elbow.Keys.Count);
        Assert.IsTrue(center.HasUnregisteredChange, "値を変えると、手で変えたときと同じく未登録の変更になる");

        edits.Undo.Undo();
        Assert.AreEqual(0, blink.Keys.Count);
        Assert.AreEqual(0, elbow.Keys.Count);
        Assert.AreEqual(Vector3.Zero, center.Position);
        Assert.IsFalse(center.HasUnregisteredChange);

        edits.Undo.Redo();
        Assert.AreEqual(3, blink.Keys.Count);
        Assert.AreEqual(new Vector3(0, 1, 0), center.Position);
        Assert.AreEqual(Bend, elbow.Keys.Keys[0].Rotation);
    }

    [TestMethod]
    public void AnExceptionRestoresEverythingAndRecordsNothing()
    {
        var (model, _, _, edits) = Create();
        MorphNode blink = model.Morphs[0];
        BoneNode center = model.Bones[0];

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => edits.Batch("途中で失敗", batch =>
        {
            batch.SetMorphKey(blink, 0, 1f);
            batch.SetBonePose(center, Vector3.One, Quaternion.Identity);
            throw new InvalidOperationException("プラグインの誤り");
        }));

        Assert.AreEqual("プラグインの誤り", ex.Message, "例外はそのまま投げ直す");
        Assert.AreEqual(0, blink.Keys.Count, "途中までの変更も戻す");
        Assert.AreEqual(Vector3.Zero, center.Position);
        Assert.IsFalse(center.HasUnregisteredChange);
        Assert.IsFalse(edits.Undo.CanUndo, "履歴に積まない");
    }

    [TestMethod]
    public void WrongValuesAreErrorsAndCancelTheBatch()
    {
        var (model, _, _, edits) = Create();
        MorphNode blink = model.Morphs[0];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => edits.Batch("負のフレーム", b =>
        {
            b.SetMorphKey(blink, 2, 1f);
            b.SetMorphKey(blink, -1, 1f);
        }));
        Assert.ThrowsExactly<ArgumentException>(() => edits.Batch("数でない値", b => b.SetMorphWeight(blink, float.NaN)));
        Assert.ThrowsExactly<ArgumentException>(() => edits.Batch("長さ 0 の回転", b => b.SetBonePose(model.Bones[0], Vector3.Zero, default)));

        Assert.AreEqual(0, blink.Keys.Count);
        Assert.IsFalse(edits.Undo.CanUndo);
    }

    [TestMethod]
    public void LockedItemsAreNotChanged()
    {
        var (model, _, _, edits) = Create();
        MorphNode blink = model.Morphs[0];
        blink.IsLocked = true;

        bool changed = edits.Batch("ロックした項目", b => Assert.IsFalse(b.SetMorphKey(blink, 0, 1f)));

        Assert.IsFalse(changed);
        Assert.AreEqual(0, blink.Keys.Count);
        Assert.IsFalse(edits.Undo.CanUndo, "何も変わらなければ積まない");
    }

    [TestMethod]
    public void SettingAKeyKeepsTheInterpolationOfTheKeyAtThatFrame()
    {
        var (model, _, _, edits) = Create();
        MorphNode blink = model.Morphs[0];
        var curve = new BezierCurve(new Vector2(0.2f, 0f), new Vector2(0.8f, 1f));
        blink.Keys.Set(new MorphKey(5, 0.2f, curve));

        edits.Batch("値だけ変える", b =>
        {
            b.SetMorphKey(blink, 5, 0.9f);
            b.SetMorphKey(blink, 8, 1.5f);
        });

        Assert.IsTrue(blink.Keys.TryGet(5, out MorphKey kept));
        Assert.AreEqual(0.9f, kept.Weight);
        Assert.AreEqual(curve, kept.Curve, "同じフレームのキーの動き方は引き継ぐ");
        Assert.IsTrue(blink.Keys.TryGet(8, out MorphKey added));
        Assert.AreEqual(BezierCurve.Linear, added.Curve, "新しいキーは直線");
        Assert.AreEqual(1f, added.Weight, "値は 0〜1 に収める");
    }

    [TestMethod]
    public void RemovingKeysIsUndoable()
    {
        var (model, _, _, edits) = Create();
        BoneNode elbow = model.Bones[1];
        elbow.Keys.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        elbow.Keys.Set(new BoneKey(10, Vector3.Zero, Bend, BoneInterpolation.Linear));

        bool changed = edits.Batch("キーを消す", b =>
        {
            Assert.IsTrue(b.RemoveBoneKey(elbow, 10));
            Assert.IsFalse(b.RemoveBoneKey(elbow, 20), "無いキーは消せない");
        });

        Assert.IsTrue(changed);
        Assert.AreEqual(1, elbow.Keys.Count);
        edits.Undo.Undo();
        Assert.AreEqual(2, elbow.Keys.Count);
    }

    [TestMethod]
    public void EditsAreAppliedAtTheCurrentFrame()
    {
        var (model, playback, _, edits) = Create();
        BoneNode elbow = model.Bones[1];
        playback.Seek(10);

        edits.Batch("ひじを曲げる", b => b.SetBoneKey(elbow, 10, Vector3.Zero, Bend));

        Assert.AreEqual(Bend, elbow.Orientation, "今のフレームのキーを置くと、姿勢もキーの値になる");
        edits.Undo.Undo();
        Assert.AreEqual(Quaternion.Identity, elbow.Orientation);
    }

    [TestMethod]
    public void TheBatchCannotBeNestedOrUsedAfterItEnds()
    {
        var (model, _, _, edits) = Create();
        BatchEdit? kept = null;

        Assert.ThrowsExactly<InvalidOperationException>(() => edits.Batch("外", b =>
        {
            b.SetMorphKey(model.Morphs[0], 0, 1f);
            edits.Batch("中", _ => { });
        }));
        Assert.AreEqual(0, model.Morphs[0].Keys.Count, "中で失敗したら外の変更も戻す");

        edits.Batch("覚えておく", b => kept = b);
        Assert.IsNotNull(kept);
        Assert.IsFalse(kept.IsOpen);
        Assert.ThrowsExactly<InvalidOperationException>(() => kept.SetMorphKey(model.Morphs[0], 0, 1f));
        Assert.ThrowsExactly<InvalidOperationException>(() => kept.RemoveMorphKey(model.Morphs[0], 0));
    }

    [TestMethod]
    public void PlaybackStopsBeforeTheBatch()
    {
        var (model, playback, _, edits) = Create();
        playback.Play();

        edits.Batch("再生中の編集", b => b.SetMorphKey(model.Morphs[0], 0, 1f));

        Assert.IsFalse(playback.IsPlaying);
    }

    [TestMethod]
    public void AnOpenSessionBecomesOneUndoStepWhenCommitted()
    {
        var (model, _, _, edits) = Create();
        BoneNode center = model.Bones[0];

        BatchSession session = edits.BeginBatch("センターを動かす");
        Assert.IsTrue(edits.HasOpenBatch);
        for (int i = 1; i <= 5; i++)
        {
            session.Edit.SetBonePose(center, new Vector3(i, 0, 0), Quaternion.Identity); // ドラッグのように何度も変える
        }
        Assert.IsTrue(session.Commit());

        Assert.IsFalse(edits.HasOpenBatch);
        Assert.IsFalse(session.IsOpen);
        Assert.AreEqual(1, edits.Undo.UndoCount, "ドラッグ 1 回が 1 つの操作");
        Assert.AreEqual("センターを動かす", edits.Undo.UndoDescription);
        edits.Undo.Undo();
        Assert.AreEqual(Vector3.Zero, center.Position);
        edits.Undo.Redo();
        Assert.AreEqual(new Vector3(5, 0, 0), center.Position);
    }

    [TestMethod]
    public void CancellingOrDisposingAnOpenSessionRestoresEverything()
    {
        var (model, _, _, edits) = Create();
        BoneNode center = model.Bones[0];
        MorphNode blink = model.Morphs[0];

        BatchSession cancelled = edits.BeginBatch("取り消す");
        cancelled.Edit.SetBonePose(center, Vector3.One, Quaternion.Identity);
        cancelled.Edit.SetMorphKey(blink, 0, 1f);
        cancelled.Cancel();

        using (BatchSession disposed = edits.BeginBatch("捨てる"))
        {
            disposed.Edit.SetMorphWeight(blink, 0.5f);
        }

        Assert.AreEqual(Vector3.Zero, center.Position);
        Assert.AreEqual(0, blink.Keys.Count);
        Assert.AreEqual(0f, blink.Weight, "終えずに Dispose したら取り消す");
        Assert.IsFalse(edits.Undo.CanUndo);
        Assert.IsFalse(edits.HasOpenBatch);
        Assert.IsFalse(cancelled.Commit(), "取り消した後は積めない");
        Assert.ThrowsExactly<InvalidOperationException>(() => cancelled.Edit.SetMorphKey(blink, 0, 1f));
    }

    [TestMethod]
    public void OtherBatchesWaitUntilTheOpenSessionEnds()
    {
        var (model, _, _, edits) = Create();
        BatchSession session = edits.BeginBatch("開いたまま");

        Assert.ThrowsExactly<InvalidOperationException>(() => edits.BeginBatch("もう 1 つ"));
        Assert.ThrowsExactly<InvalidOperationException>(() => edits.Batch("まとめた編集", _ => { }));

        Assert.IsFalse(session.Commit(), "何も変えなければ積まない");
        Assert.IsFalse(edits.Undo.CanUndo);
        Assert.IsTrue(edits.Batch("終わった後は使える", b => b.SetMorphKey(model.Morphs[0], 0, 1f)));
    }
}
