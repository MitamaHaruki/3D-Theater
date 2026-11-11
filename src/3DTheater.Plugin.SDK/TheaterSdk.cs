namespace Theater3D.Plugin.Sdk;

/// <summary>3D Theater の SDK の情報（plugin.json の host と sdkVersion に書く値）。</summary>
public static class TheaterSdk
{
    /// <summary>plugin.json の host に書く名前。</summary>
    public const string HostId = "3DTheater";

    /// <summary>SDK の版（主.副）。副の版は、形を壊さずに足したときに上げる。</summary>
    public static Version Version { get; } = new(1, 0);
}

/// <summary>3D Theater の機能の名前（plugin.json の requires に書く）。</summary>
public static class TheaterCapabilities
{
    /// <summary>シーン・再生位置・選択を読む（<see cref="ITheaterScene"/>）。</summary>
    public const string SceneRead = "scene.read";

    /// <summary>再生位置を動かす（<see cref="ITheaterScene.SeekFrame"/>）。</summary>
    public const string SceneSeek = "scene.seek";
}

/// <summary>
/// 今のシーン（<c>context.GetService&lt;ITheaterScene&gt;()</c>）。画面のスレッドから使う。
/// イベントは画面のスレッドで知らせる。プラグインを無効にすると、イベントは届かなくなる。
/// 値を変えるもの（モデル・ボーン・キーの編集）は、元に戻すと合わせる形で、後の版で足す（指示書 7.2）。
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

    /// <summary>再生位置を動かす（0〜<see cref="EndFrame"/> に収める）。使うプラグインは requires に scene.seek を書く（権限ではなく、互換性の確かめに使う）。</summary>
    void SeekFrame(int frame);

    /// <summary>再生位置が変わった。</summary>
    event EventHandler? FrameChanged;

    /// <summary>選択が変わった。</summary>
    event EventHandler? SelectionChanged;

    /// <summary>モデルの追加・削除・名前の変更など、シーンの中身が変わった。</summary>
    event EventHandler? SceneChanged;
}

/// <summary>シーンのモデル 1 体の情報。</summary>
/// <param name="Name">シーンでの名前。</param>
/// <param name="FilePath">モデルのファイル。</param>
/// <param name="IsVisible">見えているか。</param>
/// <param name="BoneCount">ボーンの数。</param>
/// <param name="MorphCount">モーフの数。</param>
public sealed record ModelInfo(string Name, string FilePath, bool IsVisible, int BoneCount, int MorphCount);
