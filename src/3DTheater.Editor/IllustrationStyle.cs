using System.Numerics;

namespace Theater3D.Editor;

/// <summary>
/// イラスト風の表現（指示書 6.1「線画、塗り・影色、色調、質感テクスチャやポスト処理による 2D イラスト感」）。
/// 映像効果（<see cref="VisualEffects"/>）と同じく、作品の描き方としてプロジェクトに保存し、プレビューと書き出しにかける。
/// - 線画: 奥行きの段差と面の折れ目に線を引く（MMD の材質の輪郭線とは別。手前の物の側に引き、背景には引かない）
/// - 塗り: 明るさの段階を減らす（色合いは保つ）
/// - 影の色: 暗い所ほど、選んだ色合いに寄せる
/// - 紙の質感: 細かいむらを重ねる（背景にもかかる）
/// 太さと粒の大きさは、高さ 1080 の画像での画素で持つ（大きな画像では比例して太く・大きくする）。色は #RRGGBB。
/// </summary>
public sealed record IllustrationStyle
{
    public const float MinLineWidth = 0.5f;
    public const float MaxLineWidth = 4f;
    public const int MinPaintLevels = 2;
    public const int MaxPaintLevels = 8;
    public const float MinPaperGrain = 1f;
    public const float MaxPaperGrain = 8f;

    /// <summary>イラスト風の表現なし（新しいプロジェクト・前の版のファイル）。</summary>
    public static IllustrationStyle None { get; } = new();

    public bool LinesEnabled { get; init; }

    /// <summary>線画の太さ（高さ 1080 での画素）。</summary>
    public float LineWidth { get; init; } = 1.5f;

    /// <summary>線画の濃さ（0〜1）。</summary>
    public float LineStrength { get; init; } = 0.85f;

    /// <summary>細かさ（0〜1。大きいほど、小さな段差・ゆるい折れ目にも線を引く）。</summary>
    public float LineDetail { get; init; } = 0.5f;

    /// <summary>線画の色（#RRGGBB。初期値は焦げ茶）。</summary>
    public string LineColor { get; init; } = "#2B2222";

    public bool PaintEnabled { get; init; }

    /// <summary>塗りの段数（2〜8。黒と白を含む）。</summary>
    public int PaintLevels { get; init; } = 4;

    public bool ShadowTintEnabled { get; init; }

    /// <summary>影の色（#RRGGBB。色合いだけを使う。初期値は青紫）。</summary>
    public string ShadowColor { get; init; } = "#6A5ACD";

    /// <summary>影の色の強さ（0〜1）。</summary>
    public float ShadowTintStrength { get; init; } = 0.5f;

    public bool PaperEnabled { get; init; }

    /// <summary>紙の質感の強さ（0〜1）。</summary>
    public float PaperStrength { get; init; } = 0.4f;

    /// <summary>紙の粒の大きさ（高さ 1080 での画素）。</summary>
    public float PaperGrain { get; init; } = 2f;

    /// <summary>どれかを使うか。</summary>
    public bool Any => LinesEnabled || PaintEnabled || ShadowTintEnabled || PaperEnabled;

    /// <summary>値を範囲に収め、色を #RRGGBB にそろえたもの（読めない色・数でない値は初期値にする）。</summary>
    public IllustrationStyle Normalized()
    {
        IllustrationStyle d = None;
        return this with
        {
            LineWidth = Fit(LineWidth, MinLineWidth, MaxLineWidth, d.LineWidth),
            LineStrength = Fit(LineStrength, 0f, 1f, d.LineStrength),
            LineDetail = Fit(LineDetail, 0f, 1f, d.LineDetail),
            LineColor = FitColor(LineColor, d.LineColor),
            PaintLevels = Math.Clamp(PaintLevels, MinPaintLevels, MaxPaintLevels),
            ShadowColor = FitColor(ShadowColor, d.ShadowColor),
            ShadowTintStrength = Fit(ShadowTintStrength, 0f, 1f, d.ShadowTintStrength),
            PaperStrength = Fit(PaperStrength, 0f, 1f, d.PaperStrength),
            PaperGrain = Fit(PaperGrain, MinPaperGrain, MaxPaperGrain, d.PaperGrain),
        };
    }

    /// <summary>色（読めなければ黒）。</summary>
    public static Vector3 ColorOf(string text) => ImageExport.TryParseColor(text, out Vector3 color) ? color : Vector3.Zero;

    private static float Fit(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string FitColor(string? text, string fallback) =>
        ImageExport.TryParseColor(text, out Vector3 color) ? ImageExport.FormatColor(color) : fallback;
}
