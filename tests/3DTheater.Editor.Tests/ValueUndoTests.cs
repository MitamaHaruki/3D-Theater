using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>場面の項目でない値（物理の切り替え・終わりのフレーム・ループ）の元に戻すと、操作のまとめ。</summary>
[TestClass]
public sealed class ValueUndoTests
{
    private static (SceneDocument Scene, PlaybackService Playback, UndoService Undo, EditService Edits) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var undo = new UndoService();
        return (scene, playback, undo, new EditService(undo, animation, playback));
    }

    private sealed class Switch
    {
        public bool On { get; set; } = true;
    }

    private sealed class Disposable(string description) : IUndoableAction, IDisposable
    {
        public string Description { get; } = description;

        public bool Disposed { get; private set; }

        public void Undo() { }

        public void Redo() { }

        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public void ToggleIsUndoneAndRedone()
    {
        var (_, _, undo, edits) = Create();
        var physics = new Switch();

        Assert.IsTrue(edits.ChangeValue("物理演算を OFF にする", () => physics.On, v => physics.On = v, false));
        Assert.IsFalse(physics.On);
        Assert.AreEqual("物理演算を OFF にする", undo.UndoDescription);

        Assert.IsTrue(undo.Undo());
        Assert.IsTrue(physics.On, "元に戻すと ON");
        Assert.IsTrue(undo.Redo());
        Assert.IsFalse(physics.On, "やり直すと OFF");

        Assert.IsFalse(edits.ChangeValue("物理演算を OFF にする", () => physics.On, v => physics.On = v, false), "変わらなければ積まない");
        Assert.AreEqual(1, undo.UndoCount);
    }

    [TestMethod]
    public void PlaybackEndFrameAndLoopAreUndoable()
    {
        var (_, playback, undo, edits) = Create();
        playback.EndFrame = 300;
        playback.Loop = true;

        edits.ChangeValue("ループ再生を OFF にする", () => playback.Loop, v => playback.Loop = v, false);
        edits.ChangeValue("終わりのフレームの変更", () => playback.EndFrame, v => playback.EndFrame = v, 120, mergeKey: "playback.endFrame");
        edits.ChangeValue("終わりのフレームの変更", () => playback.EndFrame, v => playback.EndFrame = v, 150, mergeKey: "playback.endFrame");

        Assert.AreEqual(2, undo.UndoCount, "続けて変えた終わりのフレームは 1 つの操作にまとめる");
        undo.Undo();
        Assert.AreEqual(300, playback.EndFrame, "まとめた操作を戻すと、最初の値");
        Assert.IsFalse(playback.Loop);
        undo.Undo();
        Assert.IsTrue(playback.Loop);
        undo.Redo();
        undo.Redo();
        Assert.AreEqual(150, playback.EndFrame, "やり直すと、最後の値");

        // 元に戻した後の変更は、前の操作にまとめない
        undo.Undo();
        edits.ChangeValue("終わりのフレームの変更", () => playback.EndFrame, v => playback.EndFrame = v, 200, mergeKey: "playback.endFrame");
        Assert.AreEqual(2, undo.UndoCount);
        undo.Undo();
        Assert.AreEqual(300, playback.EndFrame);
    }

    [TestMethod]
    public void ChangesDuringReplayAreNotRecorded()
    {
        // 元に戻すで値を変えると、画面の表示が切り替わって、もう一度同じ値を入れに来る（メニューとツールバーのボタン）
        var (_, _, undo, edits) = Create();
        var physics = new Switch();
        void Set(bool v)
        {
            physics.On = v;
            edits.ChangeValue("物理演算の切り替え（画面から）", () => physics.On, x => physics.On = x, v);
        }
        edits.ChangeValue("物理演算を OFF にする", () => physics.On, Set, false);
        Assert.AreEqual(1, undo.UndoCount);

        undo.Undo();
        Assert.IsTrue(physics.On);
        Assert.AreEqual(0, undo.UndoCount, "元に戻している間の変更は積まない");
        Assert.IsTrue(undo.CanRedo, "やり直しも消えない");
    }

    [TestMethod]
    public void ExtendEndFrameOnlyGrowsAndIsUndoable()
    {
        var (_, playback, undo, edits) = Create();
        playback.EndFrame = 300;

        Assert.IsFalse(edits.ExtendEndFrame(200), "短くはしない");
        Assert.IsFalse(edits.ExtendEndFrame(300));
        Assert.AreEqual(0, undo.UndoCount);
        Assert.IsTrue(edits.ExtendEndFrame(480));
        Assert.AreEqual(480, playback.EndFrame);
        Assert.AreEqual("終わりのフレームの変更", undo.UndoDescription);
        undo.Undo();
        Assert.AreEqual(300, playback.EndFrame);
    }

    [TestMethod]
    public void ImportAndTheExtendedEndFrameAreUndoneTogether()
    {
        var (scene, playback, undo, edits) = Create();
        playback.EndFrame = 300;
        playback.Loop = true;
        BoneNode bone = scene.AllNodes.OfType<BoneNode>().First();
        edits.ChangeValue("ループ再生を OFF にする", () => playback.Loop, v => playback.Loop = v, false);

        using (undo.BeginGroup())
        {
            edits.Change("モーションの読み込み（踊り.vmd）", [bone], () => bone.Keys.Set(new BoneKey(450, Vector3.UnitY, Quaternion.Identity, BoneInterpolation.Linear)),
                includeKeys: true);
            Assert.IsNull(undo.Top, "まとめている途中は、前の操作に混ぜない");
            edits.ExtendEndFrame(450);
            Assert.AreEqual(1, undo.UndoCount, "閉じるまでは積まない");
        }

        Assert.AreEqual(2, undo.UndoCount, "読み込みと終わりのフレームは 1 つの操作");
        Assert.AreEqual("モーションの読み込み（踊り.vmd）", undo.UndoDescription, "説明は最初の操作");
        undo.Undo();
        Assert.AreEqual(0, bone.Keys.Count);
        Assert.AreEqual(300, playback.EndFrame, "伸ばした終わりのフレームも戻る");
        Assert.IsFalse(playback.Loop, "その前の操作は戻さない");
        undo.Redo();
        Assert.AreEqual(1, bone.Keys.Count);
        Assert.AreEqual(450, playback.EndFrame);
    }

    [TestMethod]
    public void GroupsWithOneOrNoActionAndNestedGroups()
    {
        var (_, playback, undo, edits) = Create();
        playback.EndFrame = 300;

        using (undo.BeginGroup())
        {
            edits.ExtendEndFrame(100); // 伸ばさない
        }
        Assert.AreEqual(0, undo.UndoCount, "何も積まなければ何もしない");

        using (undo.BeginGroup())
        {
            edits.ExtendEndFrame(400);
        }
        Assert.AreEqual("終わりのフレームの変更", undo.UndoDescription, "1 つならそのまま");

        using (undo.BeginGroup())
        {
            undo.Push(new Disposable("外側"));
            using (undo.BeginGroup())
            {
                undo.Push(new Disposable("内側"));
            }
            Assert.AreEqual(1, undo.UndoCount, "内側を閉じても、外側が閉じるまで積まない");
        }
        Assert.AreEqual(2, undo.UndoCount);
        Assert.AreEqual("外側", undo.UndoDescription);
    }

    [TestMethod]
    public void DroppedGroupsDisposeTheirActions()
    {
        var (_, _, undo, _) = Create();
        var first = new Disposable("1");
        var second = new Disposable("2");
        using (undo.BeginGroup())
        {
            undo.Push(first);
            undo.Push(second);
        }
        undo.Undo();
        undo.Push(new Disposable("新しい操作")); // やり直しの履歴が消える

        Assert.IsTrue(first.Disposed, "まとめた操作も破棄する");
        Assert.IsTrue(second.Disposed);
    }
}
