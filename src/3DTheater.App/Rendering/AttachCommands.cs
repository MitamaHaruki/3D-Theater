using System.Numerics;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Rendering;

/// <summary>モデルをほかのモデルのボーンに付ける・外す（元に戻せる 1 つの操作）。付けた瞬間・外した瞬間は見た目を変えない。</summary>
internal static class AttachCommands
{
    /// <summary>
    /// <paramref name="child"/> を <paramref name="parent"/> のボーン <paramref name="bone"/> に付ける（<paramref name="parent"/> が null なら外す）。
    /// 付けられない（自分・輪になる・ボーンが無い）ときと、変わらないときは false。
    /// </summary>
    public static bool SetAttachment(EditorContext context, ModelNode child, ModelNode? parent, string? bone)
    {
        context.Attachments.Update();
        Matrix4x4 world = child.WorldMatrix;
        Matrix4x4 newParent = Matrix4x4.Identity;
        ModelAttachment? next = null;
        if (parent is not null && !string.IsNullOrEmpty(bone))
        {
            ModelNode? Find(Guid id) => context.Scene.ModelsGroup.Children.OfType<ModelNode>().FirstOrDefault(m => m.Id == id);
            if (ReferenceEquals(parent, child) || ModelAttachments.WouldLoop(child, parent, Find))
            {
                AppLog.Warning($"「{child.Name}」を「{parent.Name}」に付けると輪になるため、付けません");
                return false;
            }
            if (context.Attachments.ParentMatrixOf(parent, bone) is not { } matrix)
            {
                AppLog.Warning($"「{parent.Name}」にボーン「{bone}」が無いため、付けません");
                return false;
            }
            newParent = matrix;
            next = new ModelAttachment(parent.Id, bone);
        }
        if (child.Attachment == next)
        {
            return false;
        }
        var (position, rotation, scale) = ModelAttachments.KeepWorld(world, newParent);
        string what = next is null ? $"{child.Name}をボーンから外す" : $"{child.Name}を{parent!.Name}の{bone}に付ける";
        context.Playback.Pause();
        context.Edits.Change(what, [child], () =>
        {
            child.Attachment = next;
            child.ParentMatrix = newParent;
            child.Position = position;
            child.Rotation = rotation;
            child.Scale = scale;
        });
        AppLog.Info(next is null ? $"「{child.Name}」をボーンから外しました" : $"「{child.Name}」を「{parent!.Name}」の「{bone}」に付けました");
        return true;
    }
}
