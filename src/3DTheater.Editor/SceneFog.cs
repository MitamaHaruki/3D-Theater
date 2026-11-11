using System.Numerics;
using System.Text.Json.Serialization;

namespace Theater3D.Editor;

/// <summary>
/// 霧（遠くの物ほど霧の色に溶けこませる）。作品の描き方としてプロジェクトに保存し、プレビューと書き出しにかける。
/// カメラからの距離が <see cref="Start"/> より先で霧が始まり、そこから <see cref="Visibility"/> 進むと 95% が霧の色になる。
/// <see cref="GroundOnly"/> なら、地面（高さ 0）の近くほど濃く、<see cref="Height"/> 上がるごとに薄くする（地面より下は地面と同じ）。
/// 背景が不透明なら背景も霧の色になる（透明な背景の書き出しは透明のまま）。平行投影の固定の視点にはかけない。
/// </summary>
public sealed record SceneFog
{
    public const float MaxStart = 1000f;
    public const float MinVisibility = 1f;
    public const float MaxVisibility = 5000f;
    public const float MinHeight = 0.5f;
    public const float MaxHeight = 200f;

    /// <summary>霧なし（新しいプロジェクト・前の版のファイル）。</summary>
    public static SceneFog Off { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>霧の色（#RRGGBB）。</summary>
    public string Color { get; init; } = "#B4BECD";

    /// <summary>霧が始まる距離（カメラから）。</summary>
    public float Start { get; init; } = 20f;

    /// <summary>見通せる距離（霧の始まりから、ここまで進むと 95% が霧の色になる）。</summary>
    public float Visibility { get; init; } = 200f;

    /// <summary>地面の近くほど濃くする。</summary>
    public bool GroundOnly { get; init; }

    /// <summary>地面の霧の高さ（この高さ上がるごとに、濃さが 1/e になる。人の大きさのモデルの背丈は 20 ほど）。</summary>
    public float Height { get; init; } = 8f;

    /// <summary>描画エンジンへ渡す濃さ（霧の量 = 1 - exp(-濃さ × 長さ) が、見通せる距離で 95% になる値）。</summary>
    [JsonIgnore]
    public float Density => 3f / Visibility;

    /// <summary>描画エンジンへ渡す高さの尺度（地面の近くだけでないなら 0）。</summary>
    [JsonIgnore]
    public float HeightScale => GroundOnly ? Height : 0f;

    /// <summary>霧の色（0〜1）。</summary>
    [JsonIgnore]
    public Vector3 ColorValue => IllustrationStyle.ColorOf(Color);

    /// <summary>値を範囲に収めたもの（数でない値・読めない色は初期値にする）。</summary>
    public SceneFog Normalized() => this with
    {
        Color = ImageExport.TryParseColor(Color, out Vector3 color) ? ImageExport.FormatColor(color) : Off.Color,
        Start = float.IsFinite(Start) ? Math.Clamp(Start, 0f, MaxStart) : Off.Start,
        Visibility = float.IsFinite(Visibility) ? Math.Clamp(Visibility, MinVisibility, MaxVisibility) : Off.Visibility,
        Height = float.IsFinite(Height) ? Math.Clamp(Height, MinHeight, MaxHeight) : Off.Height,
    };
}
