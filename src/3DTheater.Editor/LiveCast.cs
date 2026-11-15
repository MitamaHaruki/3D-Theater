using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// ライブモードの「キャラクター」と「背景」（設計判断記録 78 章の 2 段目）。
/// キャラクターは 1 体だけで、顔のトラッキングはキャラクターにだけ当てはめる。どのモデルがキャラクターかは、
/// トラッキングの当てはめ先（<see cref="FaceRetargetSettings.TargetModelId"/>。ライブのファイルの live.tracking に保存する）で持つ。
/// 背景は、キャラクター以外のモデルとアクセサリ（PMX ならトラッキングしないサブキャラクター）。
/// </summary>
public static class LiveCast
{
    /// <summary>
    /// キャラクターにできるかもしれないファイルか（PMX・VRM・FBX のモデル。.x・.vac のアクセサリと、ボーンの決まりの無い glTF（.glb・.gltf）はできない）。
    /// FBX は、読み込んでボーンの名前から人型のボーンが見つかったものだけ（<see cref="CanBeCharacter"/>）。
    /// </summary>
    public static bool IsCharacterFile(string? path) =>
        Path.GetExtension(path)?.ToLowerInvariant() is ".pmx" or ".vrm" or ".fbx";

    /// <summary>
    /// 読み込んだモデルをキャラクターにできるか。FBX は人型の決まりが無いので、ボーンの名前から人型のボーンが見つかった（頭のボーンがある）ものだけ
    /// （設計判断記録 83 章の 2 段目）。
    /// </summary>
    public static bool CanBeCharacter(ModelNode model) =>
        IsCharacterFile(model.FilePath)
        && (!string.Equals(Path.GetExtension(model.FilePath), ".fbx", StringComparison.OrdinalIgnoreCase) || HasHead(model));

    private static bool HasHead(ModelNode model) => FaceRigNames.Find([.. model.Bones.Select(b => b.Name)], FaceRigNames.Head) >= 0;

    /// <summary>シーンのモデル（並び順）。</summary>
    public static IReadOnlyList<ModelNode> Models(SceneDocument scene) => [.. scene.ModelsGroup.Children.OfType<ModelNode>()];

    /// <summary>キャラクター（当てはめ先のモデルがシーンに無ければ null。外した後に元に戻すと、またキャラクターになる）。</summary>
    public static ModelNode? Character(SceneDocument scene, FaceRetargetSettings settings) =>
        settings.TargetModelId is { } id ? Models(scene).FirstOrDefault(m => m.Id == id) : null;

    /// <summary>背景（キャラクター以外のモデル。並び順）。</summary>
    public static IReadOnlyList<ModelNode> Background(SceneDocument scene, FaceRetargetSettings settings)
    {
        ModelNode? character = Character(scene, settings);
        return [.. Models(scene).Where(m => !ReferenceEquals(m, character))];
    }

    /// <summary>
    /// 前の版のファイル（当てはめ先が「自動」）を開いたときに、キャラクターにするモデルを選ぶ。
    /// 頭のボーンを持つ最初の PMX（前の版の「自動」と同じ選び方）、無ければ最初の PMX。
    /// </summary>
    public static ModelNode? ChooseCharacter(IEnumerable<ModelNode> models)
    {
        ModelNode[] candidates = [.. models.Where(CanBeCharacter)];
        return candidates.FirstOrDefault(HasHead) ?? candidates.FirstOrDefault();
    }
}
