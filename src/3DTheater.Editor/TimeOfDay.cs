using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>時刻のボタン（朝・昼・夕・夜）。</summary>
public enum TimeOfDay
{
    Morning,
    Noon,
    Evening,
    Night,
}

/// <summary>
/// 時刻を変える操作（設計判断記録 87・88 章）。時刻の項目（<see cref="TimeOfDayNode"/>）の時刻を変え、サンライトをその時刻の光にする。
/// 画面の色合いは、描くときに時刻から求めて映像効果の色調補正に重ねる（<see cref="SunClock.GradeFor"/>。映像効果の値は変えない）。
/// 太陽は影を落とす（サンライトを見えるようにし、セルフシャドウを入れる）。元に戻すは 1 回。
/// </summary>
public static class TimeOfDayPresets
{
    /// <summary>初めの季節（春分）のボタンの時刻（朝 8 時・昼 12 時・夕 17 時・夜 22 時）。</summary>
    public static float HourOf(TimeOfDay time) => HourOf(time, SunDay.Default);

    /// <summary>
    /// 季節・緯度が <paramref name="day"/> の日のボタンの時刻（15 分刻み）。朝と夕は、その日の朝・夕の光の時刻（<see cref="SunClock.AnchorsFor"/>）、
    /// 昼はいつも 12 時、夜は日の入りから、夜の長さの 3 分の 1 ほど後。春分では 8 時・12 時・17 時・22 時（設計判断記録 96 章）。
    /// </summary>
    public static float HourOf(TimeOfDay time, SunDay day)
    {
        float d = day.DayLength / 12f;
        float n = (24f - day.DayLength) / 12f;
        float hour = time switch
        {
            TimeOfDay.Morning => day.Sunrise + (2f * d),
            TimeOfDay.Evening => day.Sunset - d,
            TimeOfDay.Night => day.Sunset + (4f * n),
            _ => 12f,
        };
        return MathF.Round(hour * 4f) / 4f;
    }

    /// <summary>季節の欄に出す名前（「3 月（春分）」など。21 日ごろの太陽）。</summary>
    public static string MonthLabel(int month) => Math.Clamp(month, 1, 12) switch
    {
        3 => "3 月（春分）",
        6 => "6 月（夏至）",
        9 => "9 月（秋分）",
        12 => "12 月（冬至）",
        int m => $"{m} 月",
    };

    public static string NameOf(TimeOfDay time) => time switch
    {
        TimeOfDay.Morning => "朝",
        TimeOfDay.Evening => "夕",
        TimeOfDay.Night => "夜",
        _ => "昼",
    };

    /// <summary>時刻のボタン: 時刻を <paramref name="time"/> のボタンの時刻（時刻の項目の季節・緯度に合わせる）にする。</summary>
    public static void Apply(EditService edits, AnimationService animation, RenderSettings rendering, TimeOfDay time)
    {
        ArgumentNullException.ThrowIfNull(animation);
        SetHour(edits, animation, rendering, HourOf(time, SunDay.Of(animation.Scene.TimeOfDay)), $"時刻を「{NameOf(time)}」に");
    }

    /// <summary>
    /// 時刻を <paramref name="hour"/>（時）にし、サンライトをその時刻の光にする。<paramref name="mergeKey"/> を渡すと、続けて変えたとき
    /// （欄のホイール・ドラッグ）は 1 回の元に戻すにまとめる。時刻のキーがあるときは、時刻の未登録の変更になる（自動キーならキー）。
    /// </summary>
    public static void SetHour(EditService edits, AnimationService animation, RenderSettings rendering, float hour, string description,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(rendering);
        TimeOfDayNode time = animation.Scene.TimeOfDay;
        LightNode sun = animation.Scene.MainLight;
        void Change()
        {
            time.Hour = hour;
            sun.IsVisible = true;
            animation.ApplyTimeToSun();
        }
        if (rendering.SelfShadow)
        {
            edits.Change(description, [time, sun], Change, mergeKey);
            return;
        }
        using (edits.Undo.BeginGroup())
        {
            edits.Change(description, [time, sun], Change);
            edits.ChangeSettings(description, rendering, () => rendering.SelfShadow = true);
        }
    }

    /// <summary>
    /// 昼の太陽の向き（<paramref name="noonAzimuth"/>）か、太陽の向きも時刻で動かすか（<paramref name="followsSunPath"/>）を変え、
    /// サンライトの向きを合わせる（null の値は変えない）。
    /// </summary>
    public static void SetSunPath(EditService edits, AnimationService animation, float? noonAzimuth, bool? followsSunPath, string description,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(animation);
        TimeOfDayNode time = animation.Scene.TimeOfDay;
        edits.Change(description, [time, animation.Scene.MainLight], () =>
        {
            if (noonAzimuth is { } azimuth)
            {
                time.NoonAzimuth = azimuth;
            }
            if (followsSunPath is { } follows)
            {
                time.FollowsSunPath = follows;
            }
            animation.ApplyTimeToSun();
        }, mergeKey);
    }

    /// <summary>
    /// 季節（<paramref name="month"/>）か緯度（<paramref name="latitude"/>）を変え、サンライトを今の時刻の光にする（null の値は変えない。設計判断記録 96 章）。
    /// 時刻そのものは変えない（同じ時刻でも、季節で太陽の高さと光が変わる）。
    /// </summary>
    public static void SetSeason(EditService edits, AnimationService animation, int? month, float? latitude, string description, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(animation);
        TimeOfDayNode time = animation.Scene.TimeOfDay;
        edits.Change(description, [time, animation.Scene.MainLight], () =>
        {
            if (month is { } m)
            {
                time.Month = m;
            }
            if (latitude is { } l)
            {
                time.Latitude = l;
            }
            animation.ApplyTimeToSun();
        }, mergeKey);
    }

    /// <summary>初めの季節（春分）で、朝・昼・夕・夜の順に <paramref name="hour"/> の次のボタンの時刻。</summary>
    public static float NextHour(float hour) => NextHour(hour, SunDay.Default);

    /// <summary>朝・昼・夕・夜の順で、<paramref name="hour"/> の次のボタンの時刻（夜の次は朝。ボタンの時刻ちょうどなら、その次）。</summary>
    public static float NextHour(float hour, SunDay day)
    {
        float h = SunClock.Wrap(hour);
        foreach (TimeOfDay time in new[] { TimeOfDay.Morning, TimeOfDay.Noon, TimeOfDay.Evening, TimeOfDay.Night })
        {
            if (HourOf(time, day) > h + 1e-3f)
            {
                return HourOf(time, day);
            }
        }
        return HourOf(TimeOfDay.Morning, day);
    }

    /// <summary>
    /// ライブモードのキー操作: 時刻を <paramref name="hour"/>（0〜24 に収める）にし、サンライトをその時刻の光にする。配信の最中の操作なので、
    /// 元に戻すの履歴には入れず、手で変えた値ともみなさない（カメラの切り替えと同じ）。時刻かサンライトがロックされていれば変えずに false。
    /// </summary>
    public static bool SetLive(AnimationService animation, float hour)
    {
        ArgumentNullException.ThrowIfNull(animation);
        TimeOfDayNode time = animation.Scene.TimeOfDay;
        LightNode sun = animation.Scene.MainLight;
        if (!time.CanEdit || !sun.CanEdit)
        {
            return false;
        }
        using (animation.SuspendTracking())
        {
            time.Hour = SunClock.Wrap(hour);
            SunClock.ApplyToSun(sun, time);
        }
        return true;
    }

    /// <summary>
    /// 前の版（87 章）のプロジェクトを開いたとき: 時刻の項目が無いので、今のサンライトの左右の向きを昼の太陽の向きにする。
    /// 前の版の時刻のボタンは、色合いを映像効果の色調補正に書いていた。その値がボタンの値のままなら、時刻をそのボタンの時刻にして、
    /// 色調補正を使わない状態に戻す（同じ色合いが時刻から重なるので、見た目は変わらない）。
    /// </summary>
    public static void UpgradeFromOldProject(SceneDocument scene, RenderSettings rendering)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(rendering);
        TimeOfDayNode time = scene.TimeOfDay;
        time.NoonAzimuth = scene.MainLight.Rotation.Y;
        time.Hour = TimeOfDayNode.DefaultHour;
        ColorGrade grade = ColorGrade.Of(rendering.Effects);
        foreach (TimeOfDay preset in new[] { TimeOfDay.Morning, TimeOfDay.Evening, TimeOfDay.Night })
        {
            if (rendering.Effects.ColorEnabled && Near(grade, SunClock.LookAt(HourOf(preset)).Grade))
            {
                time.Hour = HourOf(preset);
                rendering.Effects = rendering.Effects with
                {
                    ColorEnabled = false,
                    Exposure = 0f,
                    Contrast = 1f,
                    Saturation = 1f,
                    Temperature = 0f,
                };
                break;
            }
        }

        static bool Near(ColorGrade a, ColorGrade b) =>
            MathF.Abs(a.Exposure - b.Exposure) < 1e-4f && MathF.Abs(a.Contrast - b.Contrast) < 1e-4f
            && MathF.Abs(a.Saturation - b.Saturation) < 1e-4f && MathF.Abs(a.Temperature - b.Temperature) < 1e-4f;
    }
}

/// <summary>
/// キー操作で選べる時刻と、移り変わる長さ（設計判断記録 89 章）。キーの決まりの名前（<see cref="HotkeyBinding.Name"/>）には、
/// 時（"17" など。小数も可）か <see cref="Next"/> を持つ。朝・昼・夕・夜の 4 つ（"8"・"12"・"17"・"22"）は、春分のボタンの時刻の名前のまま、
/// 時刻の項目の季節に合わせたボタンの時刻へ行く（冬の夕は 16 時など。設計判断記録 96 章）。
/// </summary>
public static class TimeOfDayHotkeys
{
    /// <summary>次の時刻へ（朝 → 昼 → 夕 → 夜 → 朝）。</summary>
    public const string Next = "next";

    /// <summary>選べる時刻（決まりの名前と、出す名前）。</summary>
    public static IReadOnlyList<(string Value, string Label)> Choices { get; } =
    [
        ("8", "朝"),
        ("12", "昼（12:00）"),
        ("17", "夕"),
        ("22", "夜"),
        (Next, "次の時刻へ（朝→昼→夕→夜）"),
    ];

    // 朝・昼・夕・夜の決まりの名前と、そのボタン
    private static readonly (string Value, TimeOfDay Time)[] Presets =
        [("8", TimeOfDay.Morning), ("12", TimeOfDay.Noon), ("17", TimeOfDay.Evening), ("22", TimeOfDay.Night)];

    /// <summary>選べる移り変わりの長さ（秒と、出す名前）。</summary>
    public static IReadOnlyList<(double Seconds, string Label)> Durations { get; } =
    [
        (0, "すぐ変える"),
        (2, "2 秒で移り変わる"),
        (5, "5 秒で移り変わる"),
        (10, "10 秒で移り変わる"),
        (30, "30 秒で移り変わる"),
        (60, "1 分で移り変わる"),
    ];

    /// <summary>決まりの名前の、出す名前（選べる時刻に無ければ「17:30」の形。読めなければ null）。</summary>
    public static string? Label(string? value)
    {
        foreach (var (v, label) in Choices)
        {
            if (v == value)
            {
                return label;
            }
        }
        return TryParseHour(value, out float hour) ? SunClock.FormatClock(hour) : null;
    }

    /// <summary>初めの季節（春分）で、今の時刻 <paramref name="currentHour"/> から見た行き先の時刻。</summary>
    public static float? TargetHour(string? value, float currentHour) => TargetHour(value, currentHour, SunDay.Default);

    /// <summary>
    /// 季節・緯度が <paramref name="day"/> の日に、今の時刻 <paramref name="currentHour"/> から見た、行き先の時刻（0〜24）。決まりの名前が読めなければ null。
    /// 朝・昼・夕・夜は、その季節のボタンの時刻。
    /// </summary>
    public static float? TargetHour(string? value, float currentHour, SunDay day)
    {
        if (value == Next)
        {
            return TimeOfDayPresets.NextHour(currentHour, day);
        }
        foreach (var (preset, time) in Presets)
        {
            if (value == preset)
            {
                return TimeOfDayPresets.HourOf(time, day);
            }
        }
        return TryParseHour(value, out float hour) ? SunClock.Wrap(hour) : null;
    }

    private static bool TryParseHour(string? value, out float hour) =>
        float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hour)
        && float.IsFinite(hour) && hour >= 0f && hour <= TimeOfDayNode.MaxHour;
}

/// <summary>
/// 時刻をゆっくり移り変わらせる（ライブモードのキー操作。設計判断記録 89 章）。時刻はいつも先へ進む（夜 22 時から朝 8 時へは、
/// 夜明けを通る 10 時間）。動き出しと止まりはゆっくり（なめらかな曲線）。
/// </summary>
public sealed class TimeOfDayTransition
{
    private float _from;
    private float _span;
    private double _seconds;
    private double _elapsed;

    /// <summary>移り変わっている途中か。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>行き先の時刻（0〜24）。</summary>
    public float Target { get; private set; }

    /// <summary>
    /// <paramref name="fromHour"/> から <paramref name="toHour"/> へ、<paramref name="seconds"/> 秒かけて移り始める。
    /// 秒数が 0 以下か、同じ時刻なら移らない（false。すぐ <paramref name="toHour"/> にする）。
    /// </summary>
    public bool Start(float fromHour, float toHour, double seconds)
    {
        float from = SunClock.Wrap(fromHour);
        Target = SunClock.Wrap(toHour);
        float span = Target - from;
        if (span < 0f)
        {
            span += 24f; // 先へ進む
        }
        if (!(seconds > 0) || span < 1e-4f)
        {
            IsRunning = false;
            return false;
        }
        _from = from;
        _span = span;
        _seconds = seconds;
        _elapsed = 0;
        IsRunning = true;
        return true;
    }

    /// <summary>やめる（今の時刻のまま）。</summary>
    public void Stop() => IsRunning = false;

    /// <summary>時間を <paramref name="seconds"/> 進めた後の時刻（0〜24）。移っていなければ null。行き先に着いたら止まる。</summary>
    public float? Update(double seconds)
    {
        if (!IsRunning)
        {
            return null;
        }
        _elapsed += Math.Max(0, seconds);
        double t = Math.Clamp(_elapsed / _seconds, 0, 1);
        if (t >= 1)
        {
            IsRunning = false;
            return Target;
        }
        double eased = t * t * (3 - (2 * t));
        return SunClock.Wrap(_from + (float)(_span * eased));
    }
}
