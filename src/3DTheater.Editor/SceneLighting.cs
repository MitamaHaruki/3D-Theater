using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 描画に使う照明と輪郭線の太さを、シーンから決める（指示書 6.1 のトゥーン描画）。
/// 照明は MMD と同じく平行光 1 つ。向きは照明の項目の回転で、何も回していないときに +Z を向く（カメラと同じ決まり）。
/// 色は MMD の初期値 154/255 に、照明の色と明るさを掛けたもの。照明を非表示にすると、照明なし（材質の環境色だけ）にする。
/// 「照明を追加」で足した照明（点光源・スポットライト・平行光）は <see cref="AddedLights"/> で、同じ決まりの色にする。
/// 影は、見えている照明だけが落とす（<see cref="Shadows"/>）。
/// </summary>
public static class SceneLighting
{
    /// <summary>
    /// 描画に使う、足した照明 1 つ（<see cref="Color"/> は色 × 明るさ × 154/255、<see cref="Direction"/> は光が進む向き）。
    /// <see cref="CastsShadow"/> は影を落とすか（影をレイトレーシングで求めるときだけ落とす）。
    /// </summary>
    public readonly record struct AddedLight(LightType Type, Vector3 Position, Vector3 Direction, float Range, Vector3 Color,
        float SpotAngle, float SpotSoftness, bool CastsShadow);

    /// <summary>MMD の初期の照明の明るさ（154 / 255）。</summary>
    public const float MmdIntensity = 154f / 255f;

    /// <summary>MMD の初期の照明の向き（光が進む向き (-0.5, -1, 0.5) を正規化したもの）。</summary>
    public static readonly Vector3 MmdDirection = Vector3.Normalize(new Vector3(-0.5f, -1f, 0.5f));

    /// <summary><see cref="MmdDirection"/> を向く照明の回転（度。X = 下向き 54.7°、Y = -45°）。</summary>
    public static readonly Vector3 MmdRotation = new(MathF.Asin(-MmdDirection.Y) * 180f / MathF.PI, -45f, 0f);

    /// <summary>照明の項目の回転から、光が進む向き。</summary>
    public static Vector3 DirectionOf(Vector3 rotationDegrees) =>
        Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, SceneRotation.ToQuaternion(rotationDegrees)));

    /// <summary>シーンの照明（最初の照明の項目）の向きと色。照明が無ければ MMD の初期値。</summary>
    public static (Vector3 Direction, Vector3 Color) From(LightNode? light)
    {
        if (light is null)
        {
            return (MmdDirection, new Vector3(MmdIntensity));
        }
        Vector3 color = light.IsEffectivelyVisible ? light.Color * (MmdIntensity * light.Intensity) : Vector3.Zero;
        return (DirectionOf(light.Rotation), color);
    }

    /// <summary>
    /// 足した照明のうち、見えている（目の印が付いている）もの。位置はワールド（照明のまとまりは動かない）、向きは回転から、
    /// 色は「色 × 明るさ × 154/255」（明るさ 1 で MMD の照明と同じ強さ）。
    /// </summary>
    public static IReadOnlyList<AddedLight> AddedLights(SceneDocument scene) =>
        [.. scene.AddedLights.Where(l => l.IsEffectivelyVisible && l.Intensity > 0f).Select(l => new AddedLight(
            l.Type, l.Position, DirectionOf(l.Rotation), l.Range, l.Color * (MmdIntensity * l.Intensity), l.SpotAngle, l.SpotSoftness,
            l.CastsShadow))];

    /// <summary>
    /// 影を落とすか（設計判断記録 86 章）。影は、見えている照明だけが落とす。
    /// <list type="bullet">
    /// <item>地面の影は、MMD の照明（<paramref name="mainLight"/>）の向きで落とすので、MMD の照明が見えているときだけ。</item>
    /// <item>セルフシャドウは、MMD の照明が見えているか、影を落とす足した照明（見えていて、明るさが 0 でなく、「影を落とす」を入れたもの。
    /// 足した照明はレイトレーシングのときだけ影を落とす）があるときだけ。</item>
    /// </list>
    /// どちらも、描画の設定（<paramref name="selfShadow"/>・<paramref name="groundShadow"/>）が入っていることが前提。
    /// MMD の照明が無いシーン（<paramref name="mainLight"/> が null）は、MMD の初めの照明が見えているとみなす。
    /// </summary>
    public static (bool SelfShadow, bool GroundShadow) Shadows(SceneDocument scene, LightNode? mainLight, bool selfShadow, bool groundShadow, bool rayTraced)
    {
        bool main = MainLightCastsShadow(mainLight);
        bool added = rayTraced && scene.AddedLights.Any(l => l.IsEffectivelyVisible && l.Intensity > 0f && l.CastsShadow);
        return (selfShadow && (main || added), groundShadow && main);
    }

    /// <summary>
    /// MMD の照明が影を落とすか（見えているか。照明の無いシーンは、MMD の初めの照明があるとみなす）。落とさないのにセルフシャドウを描くとき
    /// （足した照明だけが影を落とすとき）は、描画エンジンに MMD の照明の影を求めないよう伝える（設計判断記録 86 章）。
    /// </summary>
    public static bool MainLightCastsShadow(LightNode? mainLight) => mainLight is null || mainLight.IsEffectivelyVisible;

    /// <summary>
    /// 輪郭線の太さ 1 を何ピクセルにするか。高さ 1080 ピクセルまでは 1 ピクセル（小さなプレビューでも線が消えないように）、
    /// それより大きい画面・書き出しでは高さに比例させる（4K の書き出しでも、1080 のときと同じ割合の太さに見えるように）。
    /// </summary>
    public static float EdgePixelScale(int pixelHeight) => Math.Max(1f, pixelHeight / 1080f);
}
