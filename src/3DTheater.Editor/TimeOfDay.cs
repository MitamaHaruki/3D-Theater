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
    /// <summary>ボタンの時刻（朝 8 時・昼 12 時・夕 17 時・夜 22 時）。</summary>
    public static float HourOf(TimeOfDay time) => time switch
    {
        TimeOfDay.Morning => 8f,
        TimeOfDay.Evening => 17f,
        TimeOfDay.Night => 22f,
        _ => 12f,
    };

    public static string NameOf(TimeOfDay time) => time switch
    {
        TimeOfDay.Morning => "朝",
        TimeOfDay.Evening => "夕",
        TimeOfDay.Night => "夜",
        _ => "昼",
    };

    /// <summary>時刻のボタン: 時刻を <paramref name="time"/> のボタンの時刻にする。</summary>
    public static void Apply(EditService edits, AnimationService animation, RenderSettings rendering, TimeOfDay time) =>
        SetHour(edits, animation, rendering, HourOf(time), $"時刻を「{NameOf(time)}」に");

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
