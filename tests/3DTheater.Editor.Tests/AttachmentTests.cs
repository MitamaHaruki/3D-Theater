using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

/// <summary>モデルをほかのモデルのボーンに付ける（MMD の外部親）。</summary>
[TestClass]
public sealed class AttachmentTests
{
    private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual, string message = "")
    {
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                Assert.AreEqual(expected[r, c], actual[r, c], 1e-3f, $"{message}（{r},{c}）");
            }
        }
    }

    private static ModelNode AddModel(SceneDocument scene, string name, params string[] boneNames)
    {
        var model = new ModelNode(name, name + ".pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, boneNames.Length, 0, 0, 0), string.Empty, Vector3.Zero, Vector3.One);
        BoneNode[] bones = [.. boneNames.Select((b, i) => new BoneNode(b, i, AnimationFixtures.Movable))];
        var group = new SceneNode(SceneNodeKind.Group, "ボーン", canRename: false, hasTransform: false);
        foreach (BoneNode b in bones)
        {
            group.AddChild(b);
        }
        model.AttachParts([group], bones, []);
        scene.Add(scene.ModelsGroup, model);
        return model;
    }

    [TestMethod]
    public void AttachedModelsFollowTheParentMatrix()
    {
        var (scene, _, _, _) = AnimationFixtures.Create();
        ModelNode mic = AddModel(scene, "マイク", "全ての親");
        mic.Position = new Vector3(1, 0, 0);
        Matrix4x4 hand = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(5, 10, 0);
        mic.ParentMatrix = hand;
        AssertMatrix(mic.GetLocalMatrix(), mic.WorldMatrix, "付けていなければ、付けた先の行列は使わない");

        mic.Attachment = new ModelAttachment(Guid.NewGuid(), "右手首");
        AssertMatrix(mic.GetLocalMatrix() * hand, mic.WorldMatrix);
        Vector3 tip = Vector3.Transform(Vector3.Zero, mic.WorldMatrix);
        Assert.AreEqual(5f, tip.X, 1e-4f);
        Assert.AreEqual(10f, tip.Y, 1e-4f);
        Assert.AreEqual(-1f, tip.Z, 1e-4f, "手の向きに合わせて回る");
    }

    [TestMethod]
    public void KeepWorldMakesNoJumpWhenAttachingOrDetaching()
    {
        var model = AddModel(SceneDocument.CreateDefault(), "小物", "全ての親");
        model.Position = new Vector3(3, 4, 5);
        model.Rotation = new Vector3(10, 20, 30);
        model.Scale = new Vector3(2);
        Matrix4x4 world = model.WorldMatrix;
        Matrix4x4 parent = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f) * Matrix4x4.CreateTranslation(-2, 8, 1);

        var (position, rotation, scale) = ModelAttachments.KeepWorld(world, parent);
        model.Attachment = new ModelAttachment(Guid.NewGuid(), "頭");
        model.ParentMatrix = parent;
        model.Position = position;
        model.Rotation = rotation;
        model.Scale = scale;
        AssertMatrix(world, model.WorldMatrix, "付けた瞬間は見た目を変えない");

        var detached = ModelAttachments.KeepWorld(model.WorldMatrix, Matrix4x4.Identity);
        model.Attachment = null;
        model.Position = detached.Position;
        model.Rotation = detached.RotationDegrees;
        model.Scale = detached.Scale;
        AssertMatrix(world, model.WorldMatrix, "外した瞬間も見た目を変えない");
    }

    [TestMethod]
    public void LoopsAreDetected()
    {
        var scene = SceneDocument.CreateDefault();
        ModelNode a = AddModel(scene, "A", "全ての親");
        ModelNode b = AddModel(scene, "B", "全ての親");
        ModelNode c = AddModel(scene, "C", "全ての親");
        ModelNode? Find(Guid id) => new[] { a, b, c }.FirstOrDefault(m => m.Id == id);
        a.Attachment = new ModelAttachment(b.Id, "全ての親");

        Assert.IsTrue(ModelAttachments.WouldLoop(b, a, Find), "B を A に付けると輪（A は B に付いている）");
        Assert.IsTrue(ModelAttachments.WouldLoop(a, a, Find), "自分には付けられない");
        Assert.IsFalse(ModelAttachments.WouldLoop(b, c, Find));
        Assert.IsFalse(ModelAttachments.WouldLoop(c, a, Find), "C を A に付けるのは、A → B とつながるだけ");
    }

    [TestMethod]
    public void AttachingIsUndoable()
    {
        var (scene, playback, animation, character) = AnimationFixtures.Create();
        ModelNode mic = AddModel(scene, "マイク", "全ての親");
        var undo = new UndoService();
        var edits = new EditService(undo, animation, playback);

        edits.Change("マイクを人形の左ひじに付ける", [mic], () =>
        {
            mic.Attachment = new ModelAttachment(character.Id, "左ひじ");
            mic.Position = new Vector3(0, -1, 0);
        });
        Assert.AreEqual("左ひじ", mic.Attachment?.BoneName);
        undo.Undo();
        Assert.IsNull(mic.Attachment, "元に戻すと外れる");
        Assert.AreEqual(Vector3.Zero, mic.Position);
        undo.Redo();
        Assert.AreEqual(new ModelAttachment(character.Id, "左ひじ"), mic.Attachment);
    }

    [TestMethod]
    public void AttachmentsAreSavedAndRestored()
    {
        var (scene, playback, animation, character) = AnimationFixtures.Create();
        ModelNode mic = AddModel(scene, "マイク", "全ての親");
        mic.Attachment = new ModelAttachment(character.Id, "左ひじ");
        mic.Position = new Vector3(0.5f, -1, 0);

        ProjectFile project = ProjectCapture.Capture(scene, playback, animation, null, []);
        ModelEntry characterEntry = project.Models.Single(m => m.Name == "人形");
        ModelEntry micEntry = project.Models.Single(m => m.Name == "マイク");
        Assert.AreEqual(characterEntry.Id, micEntry.AttachTo);
        Assert.AreEqual("左ひじ", micEntry.AttachBone);
        Assert.IsNull(characterEntry.AttachTo, "付けていないモデルには書かない");
        CollectionAssert.AreEqual(new[] { 0.5f, -1f, 0f }, micEntry.Position, "付けた先から見た値のまま");

        // 開き直す: 新しいモデル（目印は新しくなる）に付け直す
        string json = ProjectSerializer.Serialize(project);
        ProjectFile read = ProjectSerializer.Deserialize(json, new LoadReport())!;
        var (scene2, _, _, character2) = AnimationFixtures.Create();
        ModelNode mic2 = AddModel(scene2, "マイク", "全ての親");
        var report = new LoadReport();
        ProjectApply.ApplyAttachments([(read.Models[0], character2), (read.Models[1], mic2)], report);
        Assert.AreEqual(new ModelAttachment(character2.Id, "左ひじ"), mic2.Attachment);
        Assert.AreEqual(0, report.WarningCount);
    }

    [TestMethod]
    public void MissingTargetsAreReportedAndLeftDetached()
    {
        var (scene, _, _, character) = AnimationFixtures.Create();
        ModelNode mic = AddModel(scene, "マイク", "全ての親");
        var entries = new List<(ModelEntry, ModelNode?)>
        {
            (new ModelEntry { Name = "人形", Id = "a" }, character),
            (new ModelEntry { Name = "マイク", Id = "b", AttachTo = "z", AttachBone = "左ひじ" }, mic),
        };
        var report = new LoadReport();
        ProjectApply.ApplyAttachments(entries, report);
        Assert.IsNull(mic.Attachment);
        StringAssert.Contains(report.Issues.Single().Message, "見つからない");

        entries[1] = (new ModelEntry { Name = "マイク", Id = "b", AttachTo = "a", AttachBone = "右足首" }, mic);
        report = new LoadReport();
        ProjectApply.ApplyAttachments(entries, report);
        Assert.IsNull(mic.Attachment);
        StringAssert.Contains(report.Issues.Single().Message, "右足首");

        // 輪になった付け方（壊れたファイル）は外す
        entries[0] = (new ModelEntry { Name = "人形", Id = "a", AttachTo = "b", AttachBone = "全ての親" }, character);
        entries[1] = (new ModelEntry { Name = "マイク", Id = "b", AttachTo = "a", AttachBone = "左ひじ" }, mic);
        report = new LoadReport();
        ProjectApply.ApplyAttachments(entries, report);
        Assert.IsTrue(character.Attachment is null || mic.Attachment is null, "どちらかを外して輪をなくす");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("輪")));
    }

    [TestMethod]
    public void ModelsWhoseTargetWasRemovedAreSavedWhereTheyAre()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        ModelNode mic = AddModel(scene, "マイク", "全ての親");
        mic.Attachment = new ModelAttachment(Guid.NewGuid(), "右手首"); // 付けた先のモデルを外した後
        mic.ParentMatrix = Matrix4x4.CreateTranslation(4, 12, 0);
        mic.Position = new Vector3(0, -1, 0);

        ModelEntry entry = ProjectCapture.Capture(scene, playback, animation, null, []).Models.Single(m => m.Name == "マイク");
        Assert.IsNull(entry.AttachTo);
        CollectionAssert.AreEqual(new[] { 4f, 11f, 0f }, entry.Position, "今の見た目の位置で、付けずに書く");
    }

    [TestMethod]
    public void BoundsAndPlacementUseTheWorldMatrix()
    {
        var scene = SceneDocument.CreateDefault();
        ModelNode mic = AddModel(scene, "マイク", "全ての親");
        mic.Attachment = new ModelAttachment(Guid.NewGuid(), "右手首");
        mic.ParentMatrix = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, 10, 0);

        var (center, radius) = SceneEvaluator.BoundingSphere(mic);
        Assert.AreEqual(new Vector3(1, 11, 1), center, "画面に収めるときも、付けた先に合わせる（箱の真ん中 0.5 を 2 倍して上へ 10）");
        Assert.AreEqual(Math.Max(0.01f, (mic.BoundsMax - mic.BoundsMin).Length() * 0.5f * 2), radius, 1e-4f);

        var (position, rotation, scaleY) = ModelAttachments.Placement(mic.WorldMatrix);
        Assert.AreEqual(new Vector3(0, 10, 0), position);
        Assert.AreEqual(2f, scaleY, 1e-4f);
        Assert.AreEqual(Quaternion.Identity, rotation);
    }
}
