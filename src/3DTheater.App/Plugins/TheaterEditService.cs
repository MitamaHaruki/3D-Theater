using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Plugin.Sdk;

namespace Theater3D.App.Plugins;

/// <summary>
/// SDK の <see cref="ITheaterEditor"/> を、アプリのまとめた編集（<see cref="EditService.Batch"/>）で実装したもの（プラグイン 1 つに 1 つ）。
/// プラグインの編集は、アプリの「元に戻す」の 1 つの操作になる（指示書 7.2「編集処理はアプリ本体の Undo/Redo と整合する API 経由で」）。
/// <see cref="Detach"/> の後は編集できない（プラグインを無効にしたとき）。
/// </summary>
internal sealed class TheaterEditService(EditorContext context) : ITheaterEditor
{
    private bool _attached = true;

    public bool Edit(string description, Action<ISceneEdit> edit)
    {
        if (!_attached)
        {
            throw new InvalidOperationException("プラグインが止まっているため、編集できません");
        }
        ArgumentNullException.ThrowIfNull(edit);
        return context.Edits.Batch(description, batch => edit(new Session(context, batch)));
    }

    /// <summary>もう編集させない。</summary>
    public void Detach() => _attached = false;

    /// <summary>1 回の <see cref="Edit"/> の間だけ使える入口（名前を項目に直して <see cref="BatchEdit"/> に渡す）。</summary>
    private sealed class Session(EditorContext context, BatchEdit batch) : ISceneEdit
    {
        public bool SetBonePose(string modelId, string bone, BonePose pose) =>
            SceneLookup.Bone(context, modelId, bone) is { } node ? batch.SetBonePose(node, pose.Translation, pose.Rotation) : Missing();

        public bool SetMorphWeight(string modelId, string morph, float weight) =>
            SceneLookup.Morph(context, modelId, morph) is { } node ? batch.SetMorphWeight(node, weight) : Missing();

        public bool SetBoneKey(string modelId, string bone, BoneKeyInfo key) =>
            SceneLookup.Bone(context, modelId, bone) is { } node ? batch.SetBoneKey(node, key.Frame, key.Translation, key.Rotation) : Missing();

        public bool SetMorphKey(string modelId, string morph, MorphKeyInfo key) =>
            SceneLookup.Morph(context, modelId, morph) is { } node ? batch.SetMorphKey(node, key.Frame, key.Weight) : Missing();

        public bool RemoveBoneKey(string modelId, string bone, int frame) =>
            SceneLookup.Bone(context, modelId, bone) is { } node ? batch.RemoveBoneKey(node, frame) : Missing();

        public bool RemoveMorphKey(string modelId, string morph, int frame) =>
            SceneLookup.Morph(context, modelId, morph) is { } node ? batch.RemoveMorphKey(node, frame) : Missing();

        // 見つからないときは false（終わった編集なら、見つからなくても誤りを知らせる）
        private bool Missing() => batch.IsOpen
            ? false
            : throw new InvalidOperationException("この編集はもう終わっています（編集の処理の中だけで使えます）");
    }
}

/// <summary>SDK で使う Id・名前から、シーンの項目を探す。</summary>
internal static class SceneLookup
{
    public static ModelNode? Model(EditorContext context, string? modelId) =>
        Guid.TryParse(modelId, out Guid id) ? context.Scene.ModelsGroup.Children.OfType<ModelNode>().FirstOrDefault(m => m.Id == id) : null;

    public static BoneNode? Bone(EditorContext context, string? modelId, string? name) =>
        name is null ? null : Model(context, modelId)?.Bones.FirstOrDefault(b => b.Name == name);

    public static MorphNode? Morph(EditorContext context, string? modelId, string? name) =>
        name is null ? null : Model(context, modelId)?.Morphs.FirstOrDefault(m => m.Name == name);

    /// <summary>選んでいる項目の属するモデル。</summary>
    public static ModelNode? SelectedModel(EditorContext context) => context.Selection.Primary switch
    {
        ModelNode model => model,
        SceneNode node => node.FindAncestor<ModelNode>(),
        _ => null,
    };
}
