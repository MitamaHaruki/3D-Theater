namespace Theater3D.Editor;

/// <summary>
/// 床の映り込み（高さ 0 の床に、モデルを鏡のように映す。MMD の床の鏡面の効果にあたる）。作品の描き方としてプロジェクトに保存し、
/// プレビューと書き出しにかける。照明・影も映した向きで当てるので、鏡に映ったのと同じ見え方になる。カメラが床より下なら映さない。
/// </summary>
public sealed record FloorReflection
{
    public const float MinFade = 0.5f;
    public const float MaxFade = 100f;

    /// <summary>映り込みなし（新しいプロジェクト・前の版のファイル）。</summary>
    public static FloorReflection Off { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>濃さ（0〜1。床のすぐ下での不透明度）。</summary>
    public float Strength { get; init; } = 0.35f;

    /// <summary>消えるまでの距離（床からの深さ。人の大きさのモデルの背丈は 20 ほど）。</summary>
    public float Fade { get; init; } = 10f;

    /// <summary>値を範囲に収めたもの（数でない値は初期値にする）。</summary>
    public FloorReflection Normalized() => this with
    {
        Strength = float.IsFinite(Strength) ? Math.Clamp(Strength, 0f, 1f) : Off.Strength,
        Fade = float.IsFinite(Fade) ? Math.Clamp(Fade, MinFade, MaxFade) : Off.Fade,
    };
}
