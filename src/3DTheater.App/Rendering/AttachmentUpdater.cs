using System.Numerics;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Rendering;

/// <summary>
/// ほかのモデルのボーンに付けたモデル（小物・アクセサリ）の、付けた先のボーンの行列（<see cref="ModelNode.ParentMatrix"/>）を、
/// 描画の前に毎回求める（ポーズを求めた後。付けた先にさらに付けた先があれば、そちらを先に）。
/// 付けた先のモデルを外した・隠した・ボーンが無いときは、最後に求めた行列のまま（その場に残る）。
/// </summary>
public sealed class AttachmentUpdater
{
    private readonly EditorContext _context;

    internal AttachmentUpdater(EditorContext context)
    {
        _context = context;
        context.RenderLoop.BeforeRender += (_, _) => Update(); // ポーズ（PoseUpdater）の後に登録しているので、今の姿勢で求める
    }

    /// <summary>付けたモデルの行列を求め直す（書き出し・当たり判定の前にも呼ぶ）。</summary>
    public void Update()
    {
        ModelNode[] models = [.. _context.Scene.ModelsGroup.Children.OfType<ModelNode>()];
        if (!models.Any(m => m.Attachment is not null))
        {
            return;
        }
        var done = new HashSet<ModelNode>();
        foreach (ModelNode model in models)
        {
            Resolve(model, models, done, 0);
        }
    }

    /// <summary><paramref name="parent"/> のボーン <paramref name="boneName"/> の、今のワールドでの行列（無ければ null）。付けるときに使う。</summary>
    public Matrix4x4? ParentMatrixOf(ModelNode parent, string boneName)
    {
        Update();
        int bone = BoneIndex(parent, boneName);
        return bone >= 0 && _context.GetLoaded(parent) is { } loaded && bone < loaded.State.Global.Length
            ? ModelAttachments.ParentMatrix(loaded.State.Global[bone], parent.WorldMatrix)
            : null;
    }

    private void Resolve(ModelNode node, ModelNode[] models, HashSet<ModelNode> done, int depth)
    {
        if (node.Attachment is not { } attachment || depth > ModelAttachments.MaxDepth || !done.Add(node))
        {
            return;
        }
        ModelNode? parent = models.FirstOrDefault(m => m.Id == attachment.ModelId);
        if (parent is null || _context.GetLoaded(parent) is not { } loaded)
        {
            return;
        }
        Resolve(parent, models, done, depth + 1);
        int bone = BoneIndex(parent, attachment.BoneName);
        if (bone >= 0 && bone < loaded.State.Global.Length)
        {
            node.ParentMatrix = ModelAttachments.ParentMatrix(loaded.State.Global[bone], parent.WorldMatrix);
        }
    }

    private static int BoneIndex(ModelNode model, string name) => model.Bones.FirstOrDefault(b => b.Name == name)?.Index ?? -1;
}
