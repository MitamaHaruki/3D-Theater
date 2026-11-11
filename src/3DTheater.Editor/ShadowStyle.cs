using System.Numerics;
using System.Text.Json.Serialization;

namespace Theater3D.Editor;

/// <summary>
/// 影の見え方（影のやわらかさ・地面の影の濃さ）。作品の描き方としてプロジェクトに保存し、プレビューと書き出しにかける。
/// やわらかさは、照明を大きさのある光源として扱い、影を落とす物と受ける面が離れているほど境目を広くぼかす（接している所はくっきり）。
/// セルフシャドウと地面の影の両方にかかる（地面の影は、照明から見た奥行きで描く）。0 なら今までどおり（MMD と同じ、くっきりした影）。
/// </summary>
public sealed record ShadowStyle
{
    /// <summary>やわらかさ 1 のときの、光源の見かけの半径の tan（約 5.7 度。人の大きさのモデルで、足元の影の先がはっきりぼける）。</summary>
    public const float MaxSoftnessTangent = 0.1f;

    /// <summary>初期の見え方（くっきりした影・地面の影は半透明の黒。新しいプロジェクト・前の版のファイル）。</summary>
    public static ShadowStyle Default { get; } = new();

    /// <summary>影のやわらかさ（0〜1。セルフシャドウと地面の影）。</summary>
    public float Softness { get; init; }

    /// <summary>地面の影の濃さ（0〜1。不透明度）。</summary>
    public float GroundOpacity { get; init; } = 0.5f;

    /// <summary>
    /// セルフシャドウを、照明から見た奥行きの画像の代わりにレイトレーシング（照明の向きへ光線を出して、影を落とす物に当たるか調べる）で
    /// 求める。奥行きの画像の細かさによらず、くっきり正確になる（顔のアップなどで、影の縁がぎざぎざにならない）。重い。
    /// やわらかさは光線を照明の見かけの円の中へ散らして出す。地面の影は変わらない。
    /// </summary>
    public bool RayTraced { get; init; }

    /// <summary>描画エンジンへ渡す、光源の見かけの半径の tan。</summary>
    [JsonIgnore]
    public float SoftnessTangent => Softness * MaxSoftnessTangent;

    /// <summary>地面の影の色（黒。a が濃さ）。</summary>
    [JsonIgnore]
    public Vector4 GroundColor => new(0f, 0f, 0f, GroundOpacity);

    /// <summary>値を範囲に収めたもの（数でない値は初期値にする）。</summary>
    public ShadowStyle Normalized() => this with
    {
        Softness = float.IsFinite(Softness) ? Math.Clamp(Softness, 0f, 1f) : Default.Softness,
        GroundOpacity = float.IsFinite(GroundOpacity) ? Math.Clamp(GroundOpacity, 0f, 1f) : Default.GroundOpacity,
    };
}
