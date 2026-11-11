using System.Numerics;

namespace Theater3D.Plugin.Sdk;

/// <summary>3D Theater の SDK の情報（plugin.json の host と sdkVersion に書く値）。</summary>
public static class TheaterSdk
{
    /// <summary>plugin.json の host に書く名前。</summary>
    public const string HostId = "3DTheater";

    /// <summary>
    /// SDK の版（主.副）。副の版は、形を壊さずに足したときに上げる。
    /// 1.0: シーン・再生位置・選択を読む。1.1: ボーン・モーフの値とキーを読む・編集する（<see cref="ITheaterEditor"/>）、モデルの Id。
    /// 1.2: ツールバー・右クリックのメニュー（<see cref="TheaterMenuPlaces"/>）・状態表示（Plugin.UI.Wpf）。
    /// 1.3: 時間のかかる処理（進み具合と取り消し）・ファイルの形式の追加（Plugin.Core の IPluginTasks・IFileFormatRegistry）。
    /// </summary>
    public static Version Version { get; } = new(1, 3);
}

/// <summary>プラグインの項目を足せる右クリックのメニュー（Plugin.UI.Wpf の PluginContextMenuItem.Place に書く。SDK 1.2 から）。</summary>
public static class TheaterMenuPlaces
{
    /// <summary>プレビュー（3D の区画）の右クリックと、区画の見出しの視点のボタンのメニュー。</summary>
    public const string Preview = "preview";

    /// <summary>シーンの一覧の行の右クリックのメニュー（右クリックした行を選んでから出す）。</summary>
    public const string SceneTree = "scene-tree";
}

/// <summary>3D Theater の機能の名前（plugin.json の requires に書く）。</summary>
public static class TheaterCapabilities
{
    /// <summary>シーン・再生位置・選択、ボーン・モーフの値とキーを読む（<see cref="ITheaterScene"/>）。</summary>
    public const string SceneRead = "scene.read";

    /// <summary>再生位置を動かす（<see cref="ITheaterScene.SeekFrame"/>）。</summary>
    public const string SceneSeek = "scene.seek";

    /// <summary>ボーン・モーフの値とキーを変える（<see cref="ITheaterEditor"/>。SDK 1.1 から）。</summary>
    public const string SceneEdit = "scene.edit";
}

/// <summary>
/// 今のシーン（<c>context.GetService&lt;ITheaterScene&gt;()</c>）。画面のスレッドから使う。
/// イベントは画面のスレッドで知らせる。プラグインを無効にすると、イベントは届かなくなる。
/// モデルは <see cref="ModelInfo.Id"/> で指す（名前は重なることがあるため）。ボーン・モーフは名前で指す（同じ名前が 2 つあれば先のもの）。
/// 値を変えるときは <see cref="ITheaterEditor"/> を使う。
/// </summary>
public interface ITheaterScene
{
    /// <summary>今のフレーム。</summary>
    int CurrentFrame { get; }

    /// <summary>作品の終わりのフレーム。</summary>
    int EndFrame { get; }

    /// <summary>1 秒あたりのフレームの数。</summary>
    int FrameRate { get; }

    /// <summary>再生中か。</summary>
    bool IsPlaying { get; }

    /// <summary>シーンのモデル（並び順）。</summary>
    IReadOnlyList<ModelInfo> Models { get; }

    /// <summary>選んでいる項目の名前（無ければ null）。</summary>
    string? SelectedName { get; }

    /// <summary>選んでいる項目の属するモデル（モデルそのものか、そのボーン・モーフなど）の Id。無ければ null（SDK 1.1 から）。</summary>
    string? SelectedModelId { get; }

    /// <summary>再生位置を動かす（0〜<see cref="EndFrame"/> に収める）。使うプラグインは requires に scene.seek を書く（権限ではなく、互換性の確かめに使う）。</summary>
    void SeekFrame(int frame);

    /// <summary>モデルのボーンの名前（並び順。モデルが無ければ空。SDK 1.1 から）。</summary>
    IReadOnlyList<string> GetBoneNames(string modelId);

    /// <summary>モデルのモーフの名前（並び順。モデルが無ければ空。SDK 1.1 から）。</summary>
    IReadOnlyList<string> GetMorphNames(string modelId);

    /// <summary>ボーンの今の移動と回転（無ければ null。SDK 1.1 から）。</summary>
    BonePose? GetBonePose(string modelId, string bone);

    /// <summary>モーフの今の値（0〜1。無ければ null。SDK 1.1 から）。</summary>
    float? GetMorphWeight(string modelId, string morph);

    /// <summary>ボーンのキー（フレームの順。無ければ空。SDK 1.1 から）。</summary>
    IReadOnlyList<BoneKeyInfo> GetBoneKeys(string modelId, string bone);

    /// <summary>モーフのキー（フレームの順。無ければ空。SDK 1.1 から）。</summary>
    IReadOnlyList<MorphKeyInfo> GetMorphKeys(string modelId, string morph);

    /// <summary>再生位置が変わった。</summary>
    event EventHandler? FrameChanged;

    /// <summary>選択が変わった。</summary>
    event EventHandler? SelectionChanged;

    /// <summary>モデルの追加・削除・名前の変更など、シーンの中身が変わった。</summary>
    event EventHandler? SceneChanged;

    /// <summary>
    /// 編集の履歴が変わった（利用者かプラグインが編集した・元に戻した・やり直した・プロジェクトを開いた。SDK 1.1 から）。
    /// キーや値を読み直すきっかけに使う。つまみのドラッグの間は何度も知らせる。
    /// </summary>
    event EventHandler? Edited;
}

/// <summary>
/// ボーン・モーフの値とキーを、アプリの「元に戻す」と合わせて変える（<c>context.GetService&lt;ITheaterEditor&gt;()</c>、
/// requires に scene.edit。SDK 1.1 から）。画面のスレッドから使う。
/// </summary>
public interface ITheaterEditor
{
    /// <summary>
    /// <paramref name="edit"/> の中の変更を、まとめて 1 つの「元に戻す」にする（メニューには <paramref name="description"/> を出す）。
    /// 何か変わったら true（何も変わらなければ履歴に積まない）。
    /// <paramref name="edit"/> が例外を投げたら、始める前の値とキーに戻してから、その例外を投げ直す。
    /// 再生中なら止めてから始める。<paramref name="edit"/> の中で、もう一度 Edit は呼べない。
    /// </summary>
    bool Edit(string description, Action<ISceneEdit> edit);
}

/// <summary>
/// <see cref="ITheaterEditor.Edit"/> の中で使う編集の入口（その処理を抜けると使えない）。
/// モデル・ボーン・モーフが無いとき、ロックしているときは変えずに false。値の誤り（負のフレーム・数でない値）は例外。
/// 値を変えると、利用者が手で変えたときと同じ扱いになる（自動キーが OFF なら未登録の変更、ON なら今のフレームのキー）。
/// </summary>
public interface ISceneEdit
{
    /// <summary>ボーンの今の移動と回転を変える。</summary>
    bool SetBonePose(string modelId, string bone, BonePose pose);

    /// <summary>モーフの今の値を変える（0〜1 に収める）。</summary>
    bool SetMorphWeight(string modelId, string morph, float weight);

    /// <summary>
    /// ボーンのキーを置く（同じフレームのキーは置き換える）。補間は、同じフレームにキーがあればその動き方を引き継ぎ、無ければ直線。
    /// </summary>
    bool SetBoneKey(string modelId, string bone, BoneKeyInfo key);

    /// <summary>モーフのキーを置く（値は 0〜1 に収める。補間はボーンと同じ決まり）。</summary>
    bool SetMorphKey(string modelId, string morph, MorphKeyInfo key);

    /// <summary>ボーンのキーを消す（消したら true）。</summary>
    bool RemoveBoneKey(string modelId, string bone, int frame);

    /// <summary>モーフのキーを消す（消したら true）。</summary>
    bool RemoveMorphKey(string modelId, string morph, int frame);
}

/// <summary>シーンのモデル 1 体の情報。</summary>
/// <param name="Name">シーンでの名前（ほかのモデルと重なることがある）。</param>
/// <param name="FilePath">モデルのファイル。</param>
/// <param name="IsVisible">見えているか。</param>
/// <param name="BoneCount">ボーンの数。</param>
/// <param name="MorphCount">モーフの数。</param>
public sealed record ModelInfo(string Name, string FilePath, bool IsVisible, int BoneCount, int MorphCount)
{
    /// <summary>シーンの中で一意の名前（モデルを指すときに使う。アプリを起動し直すと変わる。SDK 1.1 から）。</summary>
    public string Id { get; init; } = string.Empty;
}

/// <summary>
/// ボーンの移動と回転（MMD のボーンの移動・回転と同じ。VMD の値と同じ向き）。
/// </summary>
/// <param name="Translation">初期の位置からの移動（親のボーンから見た向き）。</param>
/// <param name="Rotation">回転（親のボーンから見た向き）。</param>
public readonly record struct BonePose(Vector3 Translation, Quaternion Rotation)
{
    /// <summary>動かしていない姿勢。</summary>
    public static BonePose Identity { get; } = new(Vector3.Zero, Quaternion.Identity);
}

/// <summary>ボーンのキー 1 つ（補間はアプリが持つ。SDK では値だけを扱う）。</summary>
/// <param name="Frame">フレーム（0 以上）。</param>
/// <param name="Translation">初期の位置からの移動。</param>
/// <param name="Rotation">回転。</param>
public readonly record struct BoneKeyInfo(int Frame, Vector3 Translation, Quaternion Rotation);

/// <summary>モーフのキー 1 つ。</summary>
/// <param name="Frame">フレーム（0 以上）。</param>
/// <param name="Weight">値（0〜1）。</param>
public readonly record struct MorphKeyInfo(int Frame, float Weight);
