using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 描画に使う照明と輪郭線の太さを、シーンから決める（指示書 6.1 のトゥーン描画）。
/// 照明は MMD と同じく平行光 1 つ。向きは照明の項目の回転で、何も回していないときに +Z を向く（カメラと同じ決まり）。
/// 色は MMD の初期値 154/255 に明るさを掛けた灰色。照明を非表示にすると、照明なし（材質の環境色だけ）にする。
/// </summary>
public static class SceneLighting
{
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
        Vector3 color = light.IsEffectivelyVisible ? new Vector3(MmdIntensity * light.Intensity) : Vector3.Zero;
        return (DirectionOf(light.Rotation), color);
    }

    /// <summary>
    /// 輪郭線の太さ 1 を何ピクセルにするか。高さ 1080 ピクセルまでは 1 ピクセル（小さなプレビューでも線が消えないように）、
    /// それより大きい画面・書き出しでは高さに比例させる（4K の書き出しでも、1080 のときと同じ割合の太さに見えるように）。
    /// </summary>
    public static float EdgePixelScale(int pixelHeight) => Math.Max(1f, pixelHeight / 1080f);
}
