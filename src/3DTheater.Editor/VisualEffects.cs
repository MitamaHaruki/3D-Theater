namespace Theater3D.Editor;

/// <summary>
/// 映像効果（色調補正・ブルーム・周辺減光）。作品の描き方としてプロジェクトに保存し、プレビューと書き出しの両方にかける。
/// 3 つはそれぞれ ON / OFF でき、OFF の間も値は覚えておく。値は <see cref="Normalized"/> で範囲に収める。
/// 背景・格子・選択の枠・ボーン・ギズモには、色調補正とブルームはかからない（周辺減光は背景にもかかる）。
/// </summary>
public sealed record VisualEffects
{
    public const float MinExposure = -3f;
    public const float MaxExposure = 3f;
    public const float MaxContrast = 2f;
    public const float MaxSaturation = 2f;
    public const float MaxBloomIntensity = 3f;

    /// <summary>映像効果なし（新しいプロジェクト・前の版のファイル）。</summary>
    public static VisualEffects None { get; } = new();

    /// <summary>色調補正を使う。</summary>
    public bool ColorEnabled { get; init; }

    /// <summary>明るさ（段。0 で変えない。+1 で 2 倍）。</summary>
    public float Exposure { get; init; }

    /// <summary>コントラスト（1 で変えない。0〜2）。</summary>
    public float Contrast { get; init; } = 1f;

    /// <summary>彩度（1 で変えない。0 で白黒。0〜2）。</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>色温度（0 で変えない。-1 で青く、+1 で黄色く）。</summary>
    public float Temperature { get; init; }

    /// <summary>ブルーム（明るい所が周りへにじむ光）を使う。</summary>
    public bool BloomEnabled { get; init; }

    /// <summary>ブルームの強さ（0〜3）。</summary>
    public float BloomIntensity { get; init; } = 0.6f;

    /// <summary>光り始める明るさ（0〜1。r・g・b のいちばん大きい値で比べる）。</summary>
    public float BloomThreshold { get; init; } = 0.8f;

    /// <summary>広がり（0〜1。大きいほど遠くまで広がる）。</summary>
    public float BloomSpread { get; init; } = 0.7f;

    /// <summary>周辺減光（画面の四隅を暗くする）を使う。</summary>
    public bool VignetteEnabled { get; init; }

    /// <summary>周辺減光の強さ（0〜1。四隅の暗さ）。</summary>
    public float Vignette { get; init; } = 0.35f;

    /// <summary>どれかの効果を使うか。</summary>
    public bool Any => ColorEnabled || BloomEnabled || VignetteEnabled;

    /// <summary>値を範囲に収めたもの（数でない値は初期値にする。壊れたファイル・入力の誤りに備える）。</summary>
    public VisualEffects Normalized()
    {
        VisualEffects d = None;
        return this with
        {
            Exposure = Fit(Exposure, MinExposure, MaxExposure, d.Exposure),
            Contrast = Fit(Contrast, 0f, MaxContrast, d.Contrast),
            Saturation = Fit(Saturation, 0f, MaxSaturation, d.Saturation),
            Temperature = Fit(Temperature, -1f, 1f, d.Temperature),
            BloomIntensity = Fit(BloomIntensity, 0f, MaxBloomIntensity, d.BloomIntensity),
            BloomThreshold = Fit(BloomThreshold, 0f, 1f, d.BloomThreshold),
            BloomSpread = Fit(BloomSpread, 0f, 1f, d.BloomSpread),
            Vignette = Fit(Vignette, 0f, 1f, d.Vignette),
        };
    }

    private static float Fit(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
