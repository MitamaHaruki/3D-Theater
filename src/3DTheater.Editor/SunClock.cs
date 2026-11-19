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
/// ある季節・緯度の 1 日の太陽の決まり（設計判断記録 96 章）。<see cref="Declination"/> は太陽の赤緯（度。春分・秋分 0、夏至 +23.44、冬至 -23.44）、
/// <see cref="Sunrise"/>・<see cref="Sunset"/> は日の出・日の入りの時刻（時。太陽の真ん中が地平線を通る時刻で、12 時をはさんで同じだけ離れる）。
/// </summary>
public readonly record struct SunDay(float Declination, float Latitude, float Sunrise, float Sunset)
{
    /// <summary>地球の自転の軸の傾き（度）。夏至・冬至の太陽の赤緯。</summary>
    public const float Tilt = 23.44f;

    /// <summary>初めの季節・緯度（春分。6 時に昇り 18 時に沈み、昼の高さは MMD の初めの照明と同じ）。</summary>
    public static SunDay Default { get; } = Of(TimeOfDayNode.DefaultMonth, TimeOfDayNode.DefaultLatitude);

    /// <summary>昼の長さ（時）。</summary>
    public float DayLength => Sunset - Sunrise;

    public static SunDay Of(TimeOfDayNode time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return Of(time.Month, time.Latitude);
    }

    /// <summary>
    /// <paramref name="month"/> 月の 21 日ごろ・緯度 <paramref name="latitude"/> 度の 1 日。赤緯は、3 月の 0 から 6 月の +23.44・9 月の 0・12 月の -23.44 へ、
    /// 1 年でなめらかに（正弦の形で）変わるとする。
    /// </summary>
    public static SunDay Of(int month, float latitude)
    {
        float declination = Tilt * SeasonSine(month);
        float phi = Math.Clamp(float.IsFinite(latitude) ? latitude : TimeOfDayNode.DefaultLatitude, TimeOfDayNode.MinLatitude, TimeOfDayNode.MaxLatitude);
        // 日の出の時角: cos H = -tan(緯度) tan(赤緯)。赤緯 0 か赤道では、ちょうど 6 時間（計算の誤差を入れない）
        float half = 6f;
        if (declination != 0f && phi != 0f)
        {
            double cos = -Math.Tan(phi * Math.PI / 180.0) * Math.Tan(declination * Math.PI / 180.0);
            half = (float)(Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI / 15.0);
        }
        return new SunDay(declination, phi, 12f - half, 12f + half);
    }

    // sin(30° × (月 - 3))。3 月・9 月はちょうど 0、6 月は 1、12 月は -1
    private static float SeasonSine(int month)
    {
        int step = (Math.Clamp(month, 1, 12) + 9) % 12; // 3 月が 0
        return step switch
        {
            0 or 6 => 0f,
            1 or 5 => 0.5f,
            2 or 4 => MathF.Sqrt(3f) / 2f,
            3 => 1f,
            7 or 11 => -0.5f,
            8 or 10 => -MathF.Sqrt(3f) / 2f,
            _ => -1f,
        };
    }
}

/// <summary>
/// 時刻からサンライトと画面の色合いを求める（設計判断記録 88・96 章）。
/// <list type="bullet">
/// <item>太陽は、時刻の項目の季節と緯度（<see cref="SunDay"/>）の道筋を動く。日の出に左（東）の地平線から昇り、12 時にいちばん高く、
/// 日の入りに右（西）へ沈む。初めの季節と緯度（春分・35.3°）では、6 時に昇り、12 時に MMD の初めの照明と同じ 54.7° になり、18 時に沈む。</item>
/// <item>夜（日の入りから日の出まで）は月（満月）が照らす。満月は太陽の反対にあるので、太陽と同じ時角を 12 時間遅れて、赤緯を逆にして動く
/// （夏の満月は低く、冬の満月は高い）。日の出・日の入りのころは光を弱くして、太陽と月を入れ替える。</item>
/// <item>光の色・明るさと画面の色合いは、決めた時刻の値（<see cref="AnchorsFor"/>）の間をなめらかにつなぐ。決めた時刻は、日の出・日の入りに合わせて動く。</item>
/// <item>地平線すれすれの光は、影がとても長くなるので、<see cref="MinElevation"/> より低くしない。</item>
/// </list>
/// </summary>
public static class SunClock
{
    /// <summary>光の高さの下限（度）。</summary>
    public const float MinElevation = 5f;

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

    /// <summary>
    /// 光の値を決めた時刻（時の順。0 時と 24 時は同じ）。この間は直線でつなぐ。春分（6 時に昇り 18 時に沈む）では、夜 4:30 まで・薄明 6:00・朝 8:00・
    /// 昼 10:00〜15:00・夕 17:00・薄明 18:00・夜 19:30 から。ほかの季節では、日の出・日の入りからの間を、昼（または夜）の長さに合わせて伸び縮みさせる
    /// （夏は朝が早く夕が遅い。冬はその逆）。
    /// </summary>
    public static IReadOnlyList<(float Hour, DaylightLook Look)> AnchorsFor(SunDay day)
    {
        float d = day.DayLength / 12f; // 昼の長さの、春分との比
        float n = (24f - day.DayLength) / 12f; // 夜の長さの、春分との比
        return
        [
            (0f, Night),
            (day.Sunrise - (1.5f * n), Night),
            (day.Sunrise, Twilight),
            (day.Sunrise + (2f * d), Morning),
            (day.Sunrise + (4f * d), Noon),
            (day.Sunset - (3f * d), Noon),
            (day.Sunset - d, Evening),
            (day.Sunset, Twilight),
            (day.Sunset + (1.5f * n), Night),
            (24f, Night),
        ];
    }

    /// <summary>初めの季節（春分）の、光の値を決めた時刻。</summary>
    public static IReadOnlyList<(float Hour, DaylightLook Look)> Anchors { get; } = AnchorsFor(SunDay.Default);

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

    /// <summary>初めの季節・緯度（春分）で、時刻 <paramref name="hour"/> に照らしている天体の位置。</summary>
    public static SkyPosition PositionAt(float hour) => PositionAt(hour, SunDay.Default);

    /// <summary>季節・緯度が <paramref name="day"/> の日の、時刻 <paramref name="hour"/> に照らしている天体の位置（太陽か、夜は月）。</summary>
    public static SkyPosition PositionAt(float hour, SunDay day)
    {
        float h = Wrap(hour);
        bool moon = h < day.Sunrise || h > day.Sunset; // 満月は、太陽が沈んでいる間だけ空にある（赤緯が逆なので、昼と夜の長さも逆）
        float body = moon ? Wrap(h + 12f) : h;
        float declination = moon ? -day.Declination : day.Declination;
        double hourAngle = (body - 12.0) * 15.0 * Math.PI / 180.0;
        double latitude = day.Latitude * Math.PI / 180.0;
        double delta = declination * Math.PI / 180.0;
        // sin(高さ) = sin(緯度) sin(赤緯) + cos(緯度) cos(赤緯) cos(時角)。向きは南から西へ回る角度
        double sinElevation = (Math.Sin(latitude) * Math.Sin(delta)) + (Math.Cos(latitude) * Math.Cos(delta) * Math.Cos(hourAngle));
        double elevation = Math.Asin(Math.Clamp(sinElevation, -1.0, 1.0)) * 180.0 / Math.PI;
        double azimuth = Math.Atan2(Math.Sin(hourAngle), (Math.Sin(latitude) * Math.Cos(hourAngle)) - (Math.Tan(delta) * Math.Cos(latitude))) * 180.0 / Math.PI;
        if (day.Latitude < declination)
        {
            // 昼に頭の上より北を通る（緯度の低い所の夏）: 昼の向きから見て朝が左になるよう、鏡に映して昼の向きを 0 にする（向きの決まりを変えない）
            azimuth = 180.0 - azimuth;
            azimuth = azimuth > 180.0 ? azimuth - 360.0 : azimuth;
        }
        return new SkyPosition((float)Math.Max(elevation, MinElevation), (float)azimuth, moon);
    }

    /// <summary>初めの季節（春分）の、時刻 <paramref name="hour"/> の光の色・明るさと画面の色合い。</summary>
    public static DaylightLook LookAt(float hour) => LookAt(hour, SunDay.Default);

    /// <summary>季節・緯度が <paramref name="day"/> の日の、時刻 <paramref name="hour"/> の光の色・明るさと画面の色合い。</summary>
    public static DaylightLook LookAt(float hour, SunDay day)
    {
        float h = Wrap(hour);
        IReadOnlyList<(float Hour, DaylightLook Look)> anchors = day == SunDay.Default ? Anchors : AnchorsFor(day);
        for (int i = 1; i < anchors.Count; i++)
        {
            var (end, endLook) = anchors[i];
            if (h == end)
            {
                return endLook; // 決めた時刻ちょうどは、その値のまま（計算の誤差を入れない）
            }
            if (h < end)
            {
                var (start, startLook) = anchors[i - 1];
                if (startLook == endLook)
                {
                    return endLook; // 同じ値の間（夜・昼）は、その値のまま
                }
                float t = end > start ? (h - start) / (end - start) : 1f;
                return DaylightLook.Lerp(startLook, endLook, t);
            }
        }
        return anchors[^1].Look;
    }

    /// <summary>時刻の項目 <paramref name="time"/> の今の時刻の光（季節・緯度も合わせる）。</summary>
    public static DaylightLook LookAt(TimeOfDayNode time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return LookAt(time.Hour, SunDay.Of(time));
    }

    /// <summary>
    /// 時刻の項目 <paramref name="time"/> の時刻での、サンライトの回転（度）。X は高さ、Y は昼の太陽の向き（太陽の向きも動かすなら、
    /// 時刻の分だけ回す）、Z は <paramref name="roll"/> のまま。季節・緯度も合わせる。
    /// </summary>
    public static Vector3 SunRotation(TimeOfDayNode time, float roll = 0f)
    {
        ArgumentNullException.ThrowIfNull(time);
        return SunRotationAt(time, time.Hour, roll);
    }

    /// <summary>時刻の項目 <paramref name="time"/> の季節・緯度・昼の向きで、時刻を <paramref name="hour"/> にしたときのサンライトの回転（度）。</summary>
    public static Vector3 SunRotationAt(TimeOfDayNode time, float hour, float roll = 0f)
    {
        ArgumentNullException.ThrowIfNull(time);
        SkyPosition p = PositionAt(hour, SunDay.Of(time));
        return new Vector3(p.Elevation, time.FollowsSunPath ? time.NoonAzimuth + p.Azimuth : time.NoonAzimuth, roll);
    }

    /// <summary>サンライト <paramref name="sun"/> を、時刻の項目 <paramref name="time"/> の時刻の光（向き・高さ・色・明るさ）にする。</summary>
    public static void ApplyToSun(LightNode sun, TimeOfDayNode time)
    {
        ArgumentNullException.ThrowIfNull(sun);
        DaylightLook look = LookAt(time);
        sun.Rotation = SunRotation(time, sun.Rotation.Z);
        sun.Color = look.Color;
        sun.Intensity = look.Intensity;
    }

    /// <summary>描くときの色調補正（映像効果の色調補正に、時刻の色合いを重ねたもの）。</summary>
    public static ColorGrade GradeFor(VisualEffects effects, TimeOfDayNode? time) =>
        time is null ? ColorGrade.Of(effects) : ColorGrade.Of(effects).Then(LookAt(time).Grade);

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
