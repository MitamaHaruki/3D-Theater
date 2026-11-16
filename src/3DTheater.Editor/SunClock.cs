using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 色調補正の 4 つの値（明るさ・コントラスト・彩度・色温度。<see cref="VisualEffects"/> と同じ決まり。明るさは段、色温度は -1 で青く +1 で黄色く）。
/// </summary>
public readonly record struct ColorGrade(float Exposure, float Contrast, float Saturation, float Temperature)
{
    /// <summary>何も変えない。</summary>
    public static ColorGrade Neutral { get; } = new(0f, 1f, 1f, 0f);

    public bool IsNeutral => Exposure == 0f && Contrast == 1f && Saturation == 1f && Temperature == 0f;

    /// <summary>映像効果の色調補正の値（使わないときは何も変えない）。</summary>
    public static ColorGrade Of(VisualEffects effects) => effects.ColorEnabled
        ? new(effects.Exposure, effects.Contrast, effects.Saturation, effects.Temperature)
        : Neutral;

    public static ColorGrade Lerp(ColorGrade a, ColorGrade b, float t) => new(
        float.Lerp(a.Exposure, b.Exposure, t),
        float.Lerp(a.Contrast, b.Contrast, t),
        float.Lerp(a.Saturation, b.Saturation, t),
        float.Lerp(a.Temperature, b.Temperature, t));

    /// <summary>
    /// <paramref name="other"/> を重ねた値。明るさと色温度は足し、コントラストと彩度は掛ける（範囲は映像効果と同じに収める）。
    /// 片方が <see cref="Neutral"/> なら、もう片方のまま。
    /// </summary>
    public ColorGrade Then(ColorGrade other) => new(
        Math.Clamp(Exposure + other.Exposure, VisualEffects.MinExposure, VisualEffects.MaxExposure),
        Math.Clamp(Contrast * other.Contrast, 0f, VisualEffects.MaxContrast),
        Math.Clamp(Saturation * other.Saturation, 0f, VisualEffects.MaxSaturation),
        Math.Clamp(Temperature + other.Temperature, -1f, 1f));
}

/// <summary>ある時刻の光（サンライトの色・明るさと、画面の色合い）。</summary>
public readonly record struct DaylightLook(Vector3 Color, float Intensity, ColorGrade Grade)
{
    public static DaylightLook Lerp(DaylightLook a, DaylightLook b, float t) =>
        new(Vector3.Lerp(a.Color, b.Color, t), float.Lerp(a.Intensity, b.Intensity, t), ColorGrade.Lerp(a.Grade, b.Grade, t));
}

/// <summary>
/// 照らしている天体（太陽か月）の位置。<see cref="Elevation"/> は地平線からの高さ（度）、<see cref="Azimuth"/> は昼の太陽の向きから
/// 時計回り（真上から見て）に回った角度（度。朝は負で左、夕方は正で右）。
/// </summary>
public readonly record struct SkyPosition(float Elevation, float Azimuth, bool IsMoon);

/// <summary>
/// 時刻からサンライトと画面の色合いを求める（設計判断記録 88 章）。
/// <list type="bullet">
/// <item>太陽は、東京くらいの緯度（<see cref="Latitude"/>）の春分・秋分の日の道筋を動く。6 時に左（東）の地平線から昇り、12 時にいちばん高く
/// （MMD の初めの照明と同じ 54.7°）、18 時に右（西）へ沈む。</item>
/// <item>夜（18〜6 時）は月（満月）が照らす。月は太陽と同じ道筋を 12 時間遅れて動く。日の出・日の入りのころは光を弱くして、
/// 太陽と月を入れ替える。</item>
/// <item>光の色・明るさと画面の色合いは、決めた時刻の値（<see cref="Anchors"/>）の間をなめらかにつなぐ。</item>
/// <item>地平線すれすれの光は、影がとても長くなるので、<see cref="MinElevation"/> より低くしない。</item>
/// </list>
/// </summary>
public static class SunClock
{
    /// <summary>緯度（度）。昼の太陽の高さが MMD の初めの照明（54.73561°）と同じになる値（東京は 35.7°）。</summary>
    public const float Latitude = 90f - 54.73561f;

    /// <summary>光の高さの下限（度）。</summary>
    public const float MinElevation = 5f;

    /// <summary>日の出・日の入りの時刻（この間は太陽、外は月が照らす）。</summary>
    public const float Sunrise = 6f;

    public const float Sunset = 18f;

    /// <summary>夜（月明かり）。</summary>
    public static DaylightLook Night { get; } = Look("#8FA8FF", 0.55f, -0.9f, 1.05f, 0.7f, -0.45f);

    /// <summary>日の出・日の入りのころ（光が弱く、薄紫の空）。</summary>
    public static DaylightLook Twilight { get; } = Look("#B4A0C8", 0.35f, -0.5f, 1f, 0.8f, -0.1f);

    /// <summary>朝（低い太陽のやわらかい光と、少し暖かい色合い）。</summary>
    public static DaylightLook Morning { get; } = Look("#FFE3C8", 0.95f, 0.1f, 0.95f, 0.95f, 0.15f);

    /// <summary>昼（MMD の初めの照明と同じ白い光で、色合いを変えない）。</summary>
    public static DaylightLook Noon { get; } = new(Vector3.One, 1f, ColorGrade.Neutral);

    /// <summary>夕（とても低い太陽のだいだい色の光と、暖かく鮮やかな色合い）。</summary>
    public static DaylightLook Evening { get; } = Look("#FF9E5E", 1.1f, -0.15f, 1.05f, 1.15f, 0.45f);

    /// <summary>光の値を決めた時刻（時の順。0 時と 24 時は同じ）。この間は直線でつなぐ。</summary>
    public static IReadOnlyList<(float Hour, DaylightLook Look)> Anchors { get; } =
    [
        (0f, Night),
        (4.5f, Night),
        (Sunrise, Twilight),
        (8f, Morning),
        (10f, Noon),
        (15f, Noon),
        (17f, Evening),
        (Sunset, Twilight),
        (19.5f, Night),
        (24f, Night),
    ];

    /// <summary>0〜24 時に収めた時刻（25 時 = 1 時）。</summary>
    public static float Wrap(float hour)
    {
        if (!float.IsFinite(hour))
        {
            return TimeOfDayNode.DefaultHour;
        }
        float h = hour % 24f;
        return h < 0f ? h + 24f : h;
    }

    /// <summary>時刻 <paramref name="hour"/> に照らしている天体の位置（太陽か、夜は月）。</summary>
    public static SkyPosition PositionAt(float hour)
    {
        float h = Wrap(hour);
        bool moon = h < Sunrise || h > Sunset;
        float body = moon ? Wrap(h + 12f) : h; // 満月は太陽と同じ道筋を 12 時間遅れて動く
        double hourAngle = (body - 12.0) * 15.0 * Math.PI / 180.0;
        double latitude = Latitude * Math.PI / 180.0;
        // 春分・秋分の日（赤緯 0）: sin(高さ) = cos(緯度) cos(時角)、向きは南（昼の向き）から西へ回る角度
        double elevation = Math.Asin(Math.Cos(latitude) * Math.Cos(hourAngle)) * 180.0 / Math.PI;
        double azimuth = Math.Atan2(Math.Sin(hourAngle), Math.Sin(latitude) * Math.Cos(hourAngle)) * 180.0 / Math.PI;
        return new SkyPosition((float)Math.Max(elevation, MinElevation), (float)azimuth, moon);
    }

    /// <summary>時刻 <paramref name="hour"/> の光の色・明るさと画面の色合い。</summary>
    public static DaylightLook LookAt(float hour)
    {
        float h = Wrap(hour);
        for (int i = 1; i < Anchors.Count; i++)
        {
            var (end, endLook) = Anchors[i];
            if (h == end)
            {
                return endLook; // 決めた時刻ちょうどは、その値のまま（計算の誤差を入れない）
            }
            if (h < end)
            {
                var (start, startLook) = Anchors[i - 1];
                if (startLook == endLook)
                {
                    return endLook; // 同じ値の間（夜・昼）は、その値のまま
                }
                float t = end > start ? (h - start) / (end - start) : 1f;
                return DaylightLook.Lerp(startLook, endLook, t);
            }
        }
        return Anchors[^1].Look;
    }

    /// <summary>
    /// 時刻の項目 <paramref name="time"/> の時刻での、サンライトの回転（度）。X は高さ、Y は昼の太陽の向き（太陽の向きも動かすなら、
    /// 時刻の分だけ回す）、Z は <paramref name="roll"/> のまま。
    /// </summary>
    public static Vector3 SunRotation(TimeOfDayNode time, float roll = 0f)
    {
        SkyPosition p = PositionAt(time.Hour);
        return new Vector3(p.Elevation, time.FollowsSunPath ? time.NoonAzimuth + p.Azimuth : time.NoonAzimuth, roll);
    }

    /// <summary>サンライト <paramref name="sun"/> を、時刻の項目 <paramref name="time"/> の時刻の光（向き・高さ・色・明るさ）にする。</summary>
    public static void ApplyToSun(LightNode sun, TimeOfDayNode time)
    {
        DaylightLook look = LookAt(time.Hour);
        sun.Rotation = SunRotation(time, sun.Rotation.Z);
        sun.Color = look.Color;
        sun.Intensity = look.Intensity;
    }

    /// <summary>描くときの色調補正（映像効果の色調補正に、時刻の色合いを重ねたもの）。</summary>
    public static ColorGrade GradeFor(VisualEffects effects, TimeOfDayNode? time) =>
        time is null ? ColorGrade.Of(effects) : ColorGrade.Of(effects).Then(LookAt(time.Hour).Grade);

    /// <summary>「17:30」の形（24 時より後は「25:00（次の日の 1:00）」）。</summary>
    public static string FormatClock(float hour)
    {
        if (!float.IsFinite(hour))
        {
            return string.Empty;
        }
        int minutes = (int)MathF.Round(Math.Clamp(hour, 0f, TimeOfDayNode.MaxHour) * 60f);
        string clock = $"{minutes / 60}:{minutes % 60:00}";
        if (minutes < 24 * 60)
        {
            return clock;
        }
        int next = minutes - (24 * 60);
        return $"{clock}（次の日の {next / 60}:{next % 60:00}）";
    }

    private static DaylightLook Look(string color, float intensity, float exposure, float contrast, float saturation, float temperature) =>
        new(ImageExport.TryParseColor(color, out Vector3 c) ? c : Vector3.One, intensity, new ColorGrade(exposure, contrast, saturation, temperature));
}
