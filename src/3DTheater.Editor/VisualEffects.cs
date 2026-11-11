namespace Theater3D.Editor;

/// <summary>
/// 映像効果（色調補正・ブルーム・周辺減光・被写界深度・環境光の遮り・画面の中の反射）。作品の描き方としてプロジェクトに保存し、プレビューと書き出しの両方にかける。
/// それぞれ ON / OFF でき、OFF の間も値は覚えておく。値は <see cref="Normalized"/> で範囲に収める。
/// 背景・格子・選択の枠・ボーン・ギズモには、色調補正とブルームはかからない（周辺減光は背景にもかかる）。
/// 被写界深度はシーンのカメラの効果なので、カメラ視点の区画と書き出しにだけかける（ほかの視点では、ピントの距離に意味が無いため）。
/// </summary>
public sealed record VisualEffects
{
    public const float MinExposure = -3f;
    public const float MaxExposure = 3f;
    public const float MaxContrast = 2f;
    public const float MaxSaturation = 2f;
    public const float MaxBloomIntensity = 3f;
    public const float MaxFocusDistance = 1000f;
    public const float MaxFocusRange = 200f;
    public const float MaxDofBlur = 32f;
    public const float MinAoRadius = 0.1f;
    public const float MaxAoRadius = 10f;
    public const float MinSsrDistance = 1f;
    public const float MaxSsrDistance = 200f;

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

    /// <summary>被写界深度（ピントの距離の前後をぼかす）を使う。</summary>
    public bool DofEnabled { get; init; }

    /// <summary>ピントをカメラの注視点（注視点までの距離）に合わせる。false なら <see cref="DofFocusDistance"/>。</summary>
    public bool DofFocusOnTarget { get; init; } = true;

    /// <summary>ピントの距離（カメラから。注視点に合わせないとき）。</summary>
    public float DofFocusDistance { get; init; } = 45f;

    /// <summary>ピントの合う幅（前後の合計）。</summary>
    public float DofFocusRange { get; init; } = 4f;

    /// <summary>ぼけの大きさ（高さ 1080 の画像での、ぼけの円の半径の画素。大きな画像では比例して大きくする）。</summary>
    public float DofBlur { get; init; } = 6f;

    /// <summary>環境光の遮り（物の隙間や接する所を暗くする）を使う。</summary>
    public bool AoEnabled { get; init; }

    /// <summary>環境光の遮りの強さ（0〜1）。</summary>
    public float AoStrength { get; init; } = 0.6f;

    /// <summary>環境光の遮りの広がり（ワールドの長さ。これより離れた物は遮らない。人の大きさのモデルの背丈は 20 ほど）。</summary>
    public float AoRadius { get; init; } = 1f;

    /// <summary>
    /// 画面の中の反射（つやのある PBR の材質に、画面に映っている物を映す）を使う。トゥーンの材質と、粗い材質（粗さ 0.65 より上）は映さない。
    /// 画面に映っていない物（画面の外・物の裏）は、今までどおり環境の映り込みになる。
    /// </summary>
    public bool SsrEnabled { get; init; }

    /// <summary>画面の中の反射の強さ（0〜1）。</summary>
    public float SsrStrength { get; init; } = 1f;

    /// <summary>映る物を探す距離（ワールドの長さ。これより遠い物は映さない。人の大きさのモデルの背丈は 20 ほど）。</summary>
    public float SsrDistance { get; init; } = 30f;

    /// <summary>どれかの効果を使うか。</summary>
    public bool Any => ColorEnabled || BloomEnabled || VignetteEnabled || DofEnabled || AoEnabled || SsrEnabled;

    /// <summary>ピントの距離（注視点に合わせるときは、カメラの注視点までの距離）。</summary>
    public float FocusDistanceFor(float cameraTargetDistance) => DofFocusOnTarget ? cameraTargetDistance : DofFocusDistance;

    /// <summary>ピントの合う幅の外で、ぼけが最大になるまでの距離（ピントの距離の半分。近くほど急にぼける）。</summary>
    public static float DofTransitionFor(float focusDistance) => Math.Max(1f, focusDistance * 0.5f);

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
            DofFocusDistance = Fit(DofFocusDistance, 0.5f, MaxFocusDistance, d.DofFocusDistance),
            DofFocusRange = Fit(DofFocusRange, 0f, MaxFocusRange, d.DofFocusRange),
            DofBlur = Fit(DofBlur, 0f, MaxDofBlur, d.DofBlur),
            AoStrength = Fit(AoStrength, 0f, 1f, d.AoStrength),
            AoRadius = Fit(AoRadius, MinAoRadius, MaxAoRadius, d.AoRadius),
            SsrStrength = Fit(SsrStrength, 0f, 1f, d.SsrStrength),
            SsrDistance = Fit(SsrDistance, MinSsrDistance, MaxSsrDistance, d.SsrDistance),
        };
    }

    private static float Fit(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
