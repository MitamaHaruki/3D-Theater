using System.Numerics;

namespace Theater3D.Engine.Interop;

/// <summary>tt_engine.h の TT_FRAME_* と同じ値。</summary>
[Flags]
public enum FrameFlags : uint
{
    None = 0,
    ShowGrid = 1u << 0,
    ShowAxisX = 1u << 1,
    ShowAxisY = 1u << 2,
    ShowAxisZ = 1u << 3,
    ShowTestObject = 1u << 4,
    TestObjectSelected = 1u << 5,

    /// <summary><see cref="FrameParameters.LightDirection"/> と <see cref="FrameParameters.LightColor"/> を使う（無ければ MMD の初期の照明）。</summary>
    UseLight = 1u << 6,

    /// <summary>セルフシャドウ（モデルに落ちる影）を描く。</summary>
    SelfShadow = 1u << 7,

    /// <summary>地面の影（高さ 0 の床に落ちる影）を <see cref="FrameParameters.GroundShadowColor"/> で描く。</summary>
    GroundShadow = 1u << 8,

    /// <summary>格子と座標軸を、奥行きに関係なくいちばん奥に描く（モデルの後ろ。固定視点で使う）。</summary>
    GridBehind = 1u << 9,

    /// <summary>背景を <see cref="FrameParameters.ClearColor"/> で消す（a = 0 で透明。書き出し用）。</summary>
    ClearColor = 1u << 10,

    /// <summary>
    /// 床の映り込み（<see cref="FrameParameters.ReflectionStrength"/>・<see cref="FrameParameters.ReflectionFade"/>・
    /// <see cref="FrameParameters.ReflectionBlur"/>）。
    /// </summary>
    FloorReflection = 1u << 11,

    /// <summary>環境の画像（<see cref="FrameParameters.Environment"/>）を背景に描く（平行投影では描かない）。</summary>
    EnvironmentBackground = 1u << 12,
}

/// <summary>
/// tt_engine.h の TT_PASS_* と同じ値（レンダーパス。書き出しの合成用の画像）。None 以外では、モデルを色の計算の代わりにパスの値で描く。
/// 呼ぶ側は、映像効果・背景・床の映り込み・格子・重ね描きを切って使う。輪郭線は <see cref="Alpha"/> のときだけ描く。
/// </summary>
public enum RenderPass : uint
{
    /// <summary>普通の色。</summary>
    None = 0,

    /// <summary>白（材質とテクスチャの不透明度で重ねる。輪郭線も白）。透明度だけを使う。</summary>
    Alpha = 1,

    /// <summary>
    /// カメラから見る向きの距離を <see cref="FrameParameters.PassDepthNear"/>〜<see cref="FrameParameters.PassDepthFar"/> で 0〜1 にし、
    /// 65535 倍の整数の上位 8 ビットを赤、下位を緑に出す。
    /// </summary>
    Depth = 2,

    /// <summary>カメラから見た面の向き（右・上・奥を -1〜1 から 0〜1 にして、赤・緑・青に）。</summary>
    Normal = 3,

    /// <summary>物の番号の色（<see cref="RenderPassColors.ObjectColor"/>）。</summary>
    ObjectId = 4,

    /// <summary>物と材質の番号の色（<see cref="RenderPassColors.MaterialColor"/>）。</summary>
    MaterialId = 5,

    /// <summary>セルフシャドウの影の量（白 = 影の中）。地面の影は <see cref="FrameParameters.GroundShadowColor"/> で描く。</summary>
    Shadow = 6,

    /// <summary>材質の発光の色。</summary>
    Emission = 7,
}

/// <summary>
/// レンダーパスの物・材質の番号の色（tt_engine.h の TtRenderPass と同じ式。エンジンが描く色と 1 の位まで同じ）。
/// 書き出しで、色と物・材質の対応表を作るのに使う。
/// </summary>
public static class RenderPassColors
{
    /// <summary>番号（1 から）から決まる色（0〜255。どの成分も 64 以上なので、黒い背景と区別できる）。</summary>
    public static (byte R, byte G, byte B) IdColor(uint key)
    {
        unchecked
        {
            uint h = key * 2654435761u;
            h ^= h >> 16;
            h *= 2246822519u;
            h ^= h >> 13;
            return ((byte)(64 + ((h & 0xFF) % 192)), (byte)(64 + (((h >> 8) & 0xFF) % 192)), (byte)(64 + (((h >> 16) & 0xFF) % 192)));
        }
    }

    /// <summary>物の色。<paramref name="objectIndex"/> は描く物の並び順（1 から）。</summary>
    public static (byte R, byte G, byte B) ObjectColor(int objectIndex) => IdColor((uint)objectIndex);

    /// <summary>材質の色。<paramref name="materialIndex"/> はモデルの材質の番号（0 から）。</summary>
    public static (byte R, byte G, byte B) MaterialColor(int objectIndex, int materialIndex) =>
        IdColor((uint)((objectIndex * 4096) + materialIndex + 1));
}

/// <summary>tt_engine.h の TT_LIGHT_* と同じ値（足す照明の種類）。</summary>
public enum LightSourceKind : uint
{
    /// <summary>平行光。</summary>
    Directional = 0,

    /// <summary>点光源（全方向。届く距離で 0 になる）。</summary>
    Point = 1,

    /// <summary>スポットライト（円すい。届く距離で 0 になる）。</summary>
    Spot = 2,
}

/// <summary>
/// 足す照明（メインの照明とは別。tt_engine.h の TtLight）。<see cref="Direction"/> は光が進む向き、<see cref="Color"/> は色 × 明るさ。
/// <see cref="SpotAngle"/> は円すいの全体の角度（度）、<see cref="SpotSoftness"/> は縁のぼかし（0〜1）。影は落とさない。
/// </summary>
public readonly record struct LightSource(
    LightSourceKind Kind,
    Vector3 Position,
    Vector3 Direction,
    float Range,
    Vector3 Color,
    float SpotAngle = 40f,
    float SpotSoftness = 0.3f)
{
    /// <summary>描画エンジンが 1 枚で使う数の上限（TT_MAX_LIGHTS）。</summary>
    public const int MaxCount = 8;
}

/// <summary>tt_engine.h の TT_POST_* と同じ値（映像効果）。</summary>
[Flags]
public enum PostEffectFlags : uint
{
    None = 0,

    /// <summary>色調補正（明るさ・コントラスト・彩度・色温度）。</summary>
    Color = 1u << 0,

    /// <summary>ブルーム（明るい所が周りへにじむ光）。</summary>
    Bloom = 1u << 1,

    /// <summary>周辺減光（画面の四隅を暗くする）。</summary>
    Vignette = 1u << 2,

    /// <summary>線画（奥行きの段差と面の折れ目に線を引く）。</summary>
    Lines = 1u << 3,

    /// <summary>塗り（明るさの段階を減らす）。</summary>
    Posterize = 1u << 4,

    /// <summary>影の色（暗い所を選んだ色合いに寄せる）。</summary>
    ShadowTint = 1u << 5,

    /// <summary>紙の質感（細かいむらを重ねる。背景にもかかる）。</summary>
    Paper = 1u << 6,

    /// <summary>被写界深度（ピントの距離の前後をぼかす）。</summary>
    DepthOfField = 1u << 7,

    /// <summary>環境光の遮り（奥行きから、物の隙間や接する所を暗くする）。</summary>
    AmbientOcclusion = 1u << 8,

    /// <summary>霧（遠くの物ほど霧の色に溶けこませる。平行投影では使わない）。</summary>
    Fog = 1u << 9,

    /// <summary>画面の中の反射（つやのある PBR の材質に、画面に映っている物を映す）。</summary>
    ScreenReflections = 1u << 10,
}

/// <summary>
/// 映像効果の値（tt_engine.h の TtFrameParams の post_flags 以降）。使う効果を <see cref="Flags"/> で選ぶ。
/// <see cref="Exposure"/> は段（+1 で 2 倍の明るさ）、<see cref="Contrast"/>・<see cref="Saturation"/> は 1 で変えない、
/// <see cref="Temperature"/> は -1（青く）〜 +1（黄色く）。ブルームは強さ・光り始める明るさ（0〜1）・広がり（0〜1）。
/// <see cref="Vignette"/> は四隅の暗さ（0〜1）。背景・格子・選択の枠・重ね描きの線には、色調補正とブルームはかからない。
/// イラスト風: 線画は色・太さ（画素）・濃さ（0〜1）・細かさ（0〜1）、塗りは段数（2〜16）、影の色は色と強さ（0〜1）、
/// 紙の質感は強さ（0〜1）と粒の大きさ（画素）。線画・塗り・影の色は背景にかからない。
/// 被写界深度は、ピントの距離・ピントの合う幅（前後の合計）・ぼけの最大（画素）・最大になるまでの距離（カメラから見る向きの距離）。
/// 環境光の遮りは、強さ（0〜2）と広がり（ワールドの長さ。これより離れた物は遮らない）。
/// 霧は、色・始まる距離（カメラから）・濃さ（霧の量 = 1 - exp(-濃さ × 長さ)）・高さの尺度（0 なら高さに関係なく同じ濃さ）。
/// 画面の中の反射は、強さ（0〜1）と映る物を探す距離（ワールドの長さ）。PBR の材質の環境の映り込みを、映り込む向きに画面に映っている物の色に置き換える。
/// </summary>
public readonly record struct PostEffects(
    PostEffectFlags Flags,
    float Exposure = 0f,
    float Contrast = 1f,
    float Saturation = 1f,
    float Temperature = 0f,
    float BloomIntensity = 0f,
    float BloomThreshold = 0.8f,
    float BloomSpread = 0.7f,
    float Vignette = 0f,
    Vector3 LineColor = default,
    float LineWidth = 1f,
    float LineStrength = 1f,
    float LineDetail = 0.5f,
    int PosterizeLevels = 4,
    Vector3 ShadowTint = default,
    float ShadowTintStrength = 0f,
    float PaperStrength = 0f,
    float PaperGrain = 2f,
    float DofFocusDistance = 45f,
    float DofFocusRange = 4f,
    float DofMaxBlur = 0f,
    float DofTransition = 20f,
    float AoStrength = 0f,
    float AoRadius = 1f,
    Vector3 FogColor = default,
    float FogStart = 0f,
    float FogDensity = 0f,
    float FogHeight = 0f,
    float SsrStrength = 0f,
    float SsrMaxDistance = 30f)
{
    /// <summary>映像効果なし。</summary>
    public static PostEffects None => new(PostEffectFlags.None);
}

/// <summary>
/// 1 つのビューを 1 枚描くための条件。行列は System.Numerics と同じ「行ベクトル × 行列」で、左手系・Y が上。
/// <see cref="OverlayLines"/> はモデルより手前に描く線（ボーンの表示など。2 つの頂点で 1 本）。配列を使い回せるよう範囲で渡す。
/// 照明（<see cref="LightDirection"/> は光が進む向き）は <see cref="FrameFlags.UseLight"/> のときだけ使う。
/// <see cref="EdgePixelScale"/> は材質の輪郭線の太さ 1 を何ピクセルにするか（0 なら輪郭線を描かない）。
/// <see cref="ShadowMapSize"/> はセルフシャドウの奥行きの画像の 1 辺（0 なら 2048）。<see cref="ShadowSoftness"/> は柔らかな影の
/// 光源の見かけの半径の tan（0〜0.5。0 ならくっきり）。
/// <see cref="Environment"/> は環境の画像（PBR の材質の環境光と映り込み。トゥーンには使わない）。明るさと、Y 軸のまわりの回転（ラジアン）。
/// <see cref="ClearColor"/> は <see cref="FrameFlags.ClearColor"/> のときの背景の色。透明で描くと、読み出した色は透明度をかけ合わせた値になる。
/// <see cref="ReflectionBlur"/> は床の映り込みのぼかし（0〜1。0 ならくっきり）。床に接する所はくっきり、床から深い所ほどぼかす。
/// </summary>
public readonly record struct FrameParameters(
    FrameFlags Flags,
    Matrix4x4 View,
    Matrix4x4 Projection,
    Matrix4x4 GridWorld,
    Matrix4x4 TestObjectWorld,
    IReadOnlyList<DrawItem>? Items = null,
    ReadOnlyMemory<LineVertex> OverlayLines = default,
    Vector3 LightDirection = default,
    Vector3 LightColor = default,
    float EdgePixelScale = 0,
    Vector4 GroundShadowColor = default,
    int ShadowMapSize = 0,
    Vector4 ClearColor = default,
    PostEffects Effects = default,
    IReadOnlyList<LightSource>? Lights = null,
    float ReflectionStrength = 0f,
    float ReflectionFade = 10f,
    float ShadowSoftness = 0f,
    NativeEnvironment? Environment = null,
    float EnvironmentIntensity = 1f,
    float EnvironmentRotation = 0f,
    RenderPass Pass = RenderPass.None,
    float PassDepthNear = 0f,
    float PassDepthFar = 1f,
    float ReflectionBlur = 0f);
