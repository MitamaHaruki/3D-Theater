using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor;

/// <summary>
/// VMD の照明のキーと、シーンの照明のキーの変換（ここ 1 か所にまとめる）。
/// MMD の照明のキーは「色（各 0〜1。初期値 154/255）」と「光が進む向き（初期値 (-0.5, -1, 0.5)）」だけで、いつも直線で補間する。
/// シーンの照明は「色 × 明るさ × 154/255」が MMD の色になり（<see cref="SceneLighting"/>）、向きは回転（度。何も回していないときに +Z を向く）で表す。
/// </summary>
public static class MmdLight
{
    private const float ToDegrees = 180f / MathF.PI;

    /// <summary>
    /// 光が進む向きから、照明の回転（度。X = 下向きの角度、Y = 横の向き、Z = 0）。向きが使えなければ null。
    /// 真上・真下の向きでは Y を <paramref name="previousY"/> のままにし、Y は <paramref name="previousY"/> から 180° を超えて
    /// 飛ばないようにつなぐ（キーの間で逆回りしないように）。
    /// </summary>
    public static Vector3? RotationOf(Vector3 direction, float previousY = 0f)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) || direction.LengthSquared() < 1e-12f)
        {
            return null;
        }
        Vector3 d = Vector3.Normalize(direction);
        float x = MathF.Asin(Math.Clamp(-d.Y, -1f, 1f)) * ToDegrees;
        float y = MathF.Abs(d.Y) > 0.9999f ? previousY : MathF.Atan2(d.X, d.Z) * ToDegrees;
        float turn = (y - previousY) % 360f;
        turn = turn > 180f ? turn - 360f : turn <= -180f ? turn + 360f : turn;
        return new Vector3(x, previousY + turn, 0f);
    }

    /// <summary>シーンの照明のキーを、VMD の照明のキーにする（色は 0〜1 に収める。曲線は使わない）。</summary>
    public static VmdLightKey ToVmd(LightKey key, int frame) => new(
        frame,
        Vector3.Clamp(key.Pose.Color * (key.Pose.Intensity * SceneLighting.MmdIntensity), Vector3.Zero, Vector3.One),
        SceneLighting.DirectionOf(key.Pose.Rotation));
}

/// <summary>照明のモーションを当てはめた結果。</summary>
public sealed record LightImportResult(int Keys, int LastFrame);

public static class LightMotion
{
    /// <summary>
    /// VMD の照明のキーを、シーンの照明へ足す（同じフレームのキーは置き換える）。照明の位置・届く距離などは今の値のまま。
    /// MMD の色が 154/255 より明るいキーがあれば、全部のキーで同じ明るさ（1 より大きい）にして色を表す（補間が MMD と同じになるように）。
    /// 30 fps 以外の作品では位置を合わせ、元で 1 フレームしか離れていないキー（照明の切り替え）は「段階」にする（カメラと同じ）。
    /// </summary>
    public static LightImportResult Apply(VmdDocument vmd, LightNode light, int frameRate, LoadReport report)
    {
        double scale = frameRate / (double)MotionImport.MmdFrameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        VmdLightKey[] source = [.. vmd.LightKeys.OrderBy(k => k.Frame)];
        float brightest = source.Select(k => Finite(k.Color)).Select(c => Math.Max(c.X, Math.Max(c.Y, c.Z))).DefaultIfEmpty(0f).Max();
        float intensity = Math.Max(1f, brightest / SceneLighting.MmdIntensity);
        var keys = new List<LightKey>();
        int cuts = 0, skipped = 0;
        float previousY = light.Rotation.Y;
        VmdLightKey? previous = null;
        foreach (VmdLightKey k in source)
        {
            if (MmdLight.RotationOf(k.Direction, previousY) is not { } rotation)
            {
                skipped++;
                continue;
            }
            previousY = rotation.Y;
            Vector3 color = Vector3.Clamp(Finite(k.Color) / (SceneLighting.MmdIntensity * intensity), Vector3.Zero, Vector3.One);
            var pose = new LightPose(light.Position, rotation, color, intensity, light.Range, light.SpotAngle, light.SpotSoftness);
            var key = new LightKey(Scale(k.Frame), pose, BezierCurve.Linear);
            if (scale > 1 && previous is { } p && k.Frame - p.Frame == 1)
            {
                key = key with { Kind = KeyInterpolationKind.Step };
                cuts++;
            }
            keys.Add(key);
            previous = k;
        }
        light.Keys.SetRange(keys);

        if (scale != 1)
        {
            InfoOnce(report, $"作品のフレームの速さ（{frameRate} fps）に合わせて、キーの位置を {scale:0.##} 倍にしました（MMD のモーションは 30 fps）");
        }
        if (cuts > 0)
        {
            report.Info(LoadIssueCategory.Note, $"1 フレームで切り替わる照明（{cuts:N0} か所）は、途中の色が出ないよう段階の補間にしました");
        }
        if (intensity > 1f)
        {
            report.Info(LoadIssueCategory.Note, $"MMD の照明の色が初期値（154/255）より明るいキーがあるため、照明の明るさを {intensity:0.##} にして表しました");
        }
        if (skipped > 0)
        {
            report.Warning(LoadIssueCategory.Format, $"向きの読めない照明のキー {skipped:N0} 個は使いませんでした");
        }
        return new LightImportResult(keys.Count, light.Keys.LastFrame);
    }

    /// <summary>
    /// シーンの照明のキーを、VMD の照明のキーにする。30 fps に直し、段階の補間は 1 つ前のフレームにキーを足して表す。
    /// VMD の照明は色と向きだけで直線の補間なので、曲線・位置・届く距離などは書かない（知らせる）。
    /// </summary>
    public static IReadOnlyList<VmdLightKey> CreateKeys(LightNode light, int frameRate, LoadReport report)
    {
        double scale = MotionImport.MmdFrameRate / (double)frameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        var written = new SortedDictionary<int, VmdLightKey>();
        int steps = 0, overlapped = 0, curves = 0, bright = 0;
        LightKey? previous = null;
        foreach (LightKey key in light.Keys.Keys)
        {
            int frame = Scale(key.Frame);
            if (key.Kind == KeyInterpolationKind.Step && previous is { } p && frame - 1 > Scale(p.Frame))
            {
                written[frame - 1] = MmdLight.ToVmd(p, frame - 1);
                steps++;
            }
            if (written.ContainsKey(frame))
            {
                overlapped++;
            }
            written[frame] = MmdLight.ToVmd(key, frame);
            curves += key.Kind == KeyInterpolationKind.Curve && !key.Curve.IsLinear ? 1 : 0;
            Vector3 mmd = key.Pose.Color * (key.Pose.Intensity * SceneLighting.MmdIntensity);
            bright += Math.Max(mmd.X, Math.Max(mmd.Y, mmd.Z)) > 1.0001f ? 1 : 0;
            previous = key;
        }
        if (light.Keys.Count == 0)
        {
            return [];
        }
        if (scale != 1)
        {
            InfoOnce(report, $"作品のフレームの速さ（{frameRate} fps）から、MMD の 30 fps に直してキーの位置を {scale:0.##} 倍にしました");
        }
        if (overlapped > 0)
        {
            report.Warning(LoadIssueCategory.Note, $"30 fps に直したときに同じフレームに重なった照明のキーが {overlapped:N0} 件あり、後ろのキーを使いました");
        }
        if (steps > 0)
        {
            report.Info(LoadIssueCategory.Note, $"照明の段階の補間（{steps:N0} 件）は VMD に無いため、1 つ前のフレームにキーを足して表しました");
        }
        if (curves > 0)
        {
            report.Info(LoadIssueCategory.Note, $"照明のキーの補間曲線（{curves:N0} 件）は VMD に無いため、直線になります");
        }
        if (bright > 0)
        {
            report.Info(LoadIssueCategory.Note, $"MMD の照明より明るいキー（{bright:N0} 件）は、MMD のいちばん明るい色（255）にしました");
        }
        if (light.HasUnregisteredChange)
        {
            report.Warning(LoadIssueCategory.Note, "照明にキーに登録していない変更があり、書き出していません（書き出すには先に登録してください）");
        }
        return [.. written.Values];
    }

    /// <summary>時刻のキーを照明のキーにするときに許す、色の違い（0〜1。MMD の色の 1 段）。</summary>
    public const float TimeColorTolerance = 1f / 255f;

    /// <summary>時刻のキーを照明のキーにするときに許す、光の向きの違い（度）。</summary>
    public const float TimeAngleTolerance = 0.25f;

    /// <summary>
    /// 時刻のキーで動くサンライトを、VMD の照明のキーにする（設計判断記録 96 章）。時刻のキーが無ければ、サンライトの照明のキーを書く（<see cref="CreateKeys"/>）。
    /// <list type="bullet">
    /// <item>MMD の照明はキーの間を直線でつなぐだけなので、時刻のキーの最初から最後まで、MMD の 1 フレームごとに太陽（夜は月）の向きと色を求め、
    /// 直線でつないでも <see cref="TimeAngleTolerance"/>・<see cref="TimeColorTolerance"/> より違わない所のキーを省く（太陽が空の道筋をたどるように）。</item>
    /// <item>時刻のキーがあるときは、サンライトは時刻で決まる（このアプリでも同じ）ので、サンライトの照明のキーは書かない。</item>
    /// <item>画面の色合い（時刻が色調補正に重ねる分）は、VMD に入らない（知らせる）。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<VmdLightKey> CreateKeysFromTime(TimeOfDayNode time, LightNode sun, int frameRate, LoadReport report)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(sun);
        ArgumentNullException.ThrowIfNull(report);
        if (time.Keys.Count == 0)
        {
            return CreateKeys(sun, frameRate, report);
        }
        double scale = MotionImport.MmdFrameRate / (double)frameRate;
        int first = (int)Math.Round(time.Keys.FirstFrame * scale, MidpointRounding.AwayFromZero);
        int last = (int)Math.Round(time.Keys.LastFrame * scale, MidpointRounding.AwayFromZero);
        SunDay day = SunDay.Of(time);

        // MMD の 1 フレームごとの光（作品のフレームに直すと半端になることがある。時刻のキーは半端なフレームでも求められる）
        var samples = new List<VmdLightKey>(last - first + 1);
        for (int frame = first; frame <= last; frame++)
        {
            float hour = KeyframeEvaluator.TryEvaluate(time.Keys, (float)(frame / scale), out float h) ? h : time.Hour;
            DaylightLook look = SunClock.LookAt(hour, day);
            Vector3 color = Vector3.Clamp(look.Color * (look.Intensity * SceneLighting.MmdIntensity), Vector3.Zero, Vector3.One);
            samples.Add(new VmdLightKey(frame, color, SceneLighting.DirectionOf(SunClock.SunRotationAt(time, hour))));
        }
        IReadOnlyList<VmdLightKey> keys = Simplify(samples);

        if (scale != 1)
        {
            InfoOnce(report, $"作品のフレームの速さ（{frameRate} fps）から、MMD の 30 fps に直してキーの位置を {scale:0.##} 倍にしました");
        }
        report.Info(LoadIssueCategory.Note,
            $"時刻で動くサンライトを、照明のキー {keys.Count:N0} 個にして書き出しました（MMD の照明はキーの間を直線でつなぐので、太陽が空の道筋をたどるように細かく分けました）");
        report.Info(LoadIssueCategory.Note, "時刻の画面の色合い（映像効果の色調補正に重ねる分）は、VMD に入りません");
        if (sun.Keys.Count > 0)
        {
            report.Info(LoadIssueCategory.Note,
                $"サンライトの照明のキー（{sun.Keys.Count:N0} 件）は、時刻のキーが優先されるため書き出していません（このアプリでも、時刻のキーがあるときはサンライトは時刻で決まります）");
        }
        if (time.HasUnregisteredChange)
        {
            report.Warning(LoadIssueCategory.Note, "時刻にキーに登録していない変更があり、書き出していません（書き出すには先に登録してください）");
        }
        if (sun.HasUnregisteredChange)
        {
            report.Warning(LoadIssueCategory.Note, "サンライトに手で変えた変更があり、書き出していません（時刻のキーから求めた光を書き出しました）");
        }
        return keys;
    }

    /// <summary>
    /// 1 フレームごとの照明のキーから、前後のキーを直線でつないでも違いが許しの中に収まるキーを省く（両端は残す）。
    /// 許しを超える所でいちばん違うキーを残して、その左右を同じように調べる（Douglas–Peucker の方法）。
    /// </summary>
    internal static IReadOnlyList<VmdLightKey> Simplify(IReadOnlyList<VmdLightKey> samples)
    {
        if (samples.Count <= 2)
        {
            return [.. samples];
        }
        var keep = new bool[samples.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, samples.Count - 1));
        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end - start < 2)
            {
                continue;
            }
            VmdLightKey a = samples[start], b = samples[end];
            int worst = -1;
            float worstError = 0f;
            for (int i = start + 1; i < end; i++)
            {
                float t = (samples[i].Frame - a.Frame) / (float)(b.Frame - a.Frame);
                // MMD は向きも色も直線でつなぐ（向きは、つないだ後の向きで照らす）
                Vector3 color = Vector3.Lerp(a.Color, b.Color, t);
                Vector3 direction = Vector3.Lerp(a.Direction, b.Direction, t);
                float colorError = MaxAbs(color - samples[i].Color) / TimeColorTolerance;
                // 向きの違いの角度（2 つの向きの先の間の長さから求める。小さい角度でも正確）。いちばん違う所で分けるので、大きな違いも数のまま比べる
                float angle = direction.LengthSquared() > 1e-12f
                    ? 2f * MathF.Asin(Math.Min(1f, Vector3.Distance(Vector3.Normalize(direction), Vector3.Normalize(samples[i].Direction)) / 2f)) * 180f / MathF.PI
                    : 180f;
                float error = MathF.Max(colorError, angle / TimeAngleTolerance);
                if (error > worstError)
                {
                    worstError = error;
                    worst = i;
                }
            }
            if (worst >= 0 && worstError > 1f)
            {
                keep[worst] = true;
                stack.Push((start, worst));
                stack.Push((worst, end));
            }
        }
        var kept = new List<VmdLightKey>();
        for (int i = 0; i < samples.Count; i++)
        {
            if (keep[i])
            {
                kept.Add(samples[i]);
            }
        }
        return kept;

        static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
    }

    private static Vector3 Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? Vector3.Max(v, Vector3.Zero) : Vector3.Zero;

    // カメラと照明を一緒に読み書きするとき、同じ知らせを 2 度出さない
    private static void InfoOnce(LoadReport report, string message)
    {
        if (!report.Issues.Any(i => i.Message == message))
        {
            report.Info(LoadIssueCategory.Note, message);
        }
    }
}

/// <summary>セルフ影のモーションを当てはめた結果。</summary>
public sealed record SelfShadowImportResult(int Keys, int LastFrame);

/// <summary>
/// VMD のセルフ影のキーと、シーンのセルフ影のキーの変換。このアプリは影の範囲をいつも自動で決め、モード 2 も無いので、
/// モード 0 を「セルフシャドウなし」、モード 1・2 を「あり」として使い、モードと影範囲の値は書き戻すために持つ。
/// </summary>
public static class SelfShadowMotion
{
    /// <summary>VMD のセルフ影のキーを足す（同じフレームのキーは置き換える）。30 fps 以外の作品では位置を合わせる。</summary>
    public static SelfShadowImportResult Apply(VmdDocument vmd, SelfShadowNode node, int frameRate, LoadReport report)
    {
        double scale = frameRate / (double)MotionImport.MmdFrameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        var keys = new List<SelfShadowKey>();
        int mode2 = 0, unknown = 0;
        foreach (VmdShadowKey k in vmd.ShadowKeys.OrderBy(k => k.Frame))
        {
            byte mode = k.Mode;
            if (mode > 2)
            {
                unknown++;
                mode = 1;
            }
            mode2 += mode == 2 ? 1 : 0;
            keys.Add(new SelfShadowKey(Scale(k.Frame), mode, float.IsFinite(k.Distance) ? k.Distance : SelfShadowKey.DefaultDistance));
        }
        node.Keys.SetRange(keys);
        if (scale != 1)
        {
            InfoOnce(report, $"作品のフレームの速さ（{frameRate} fps）に合わせて、キーの位置を {scale:0.##} 倍にしました（MMD のモーションは 30 fps）");
        }
        if (mode2 > 0)
        {
            report.Info(LoadIssueCategory.Unsupported, $"セルフ影のモード 2（{mode2:N0} 件）は、モード 1 と同じに描きます");
        }
        if (unknown > 0)
        {
            report.Warning(LoadIssueCategory.Format, $"分からないセルフ影のモード（{unknown:N0} 件）は、モード 1 にしました");
        }
        if (keys.Count > 0)
        {
            report.Info(LoadIssueCategory.Note, "セルフ影の影範囲は使いません（影の範囲はいつも自動で決めます。値は書き出しのために残します）");
        }
        return new SelfShadowImportResult(keys.Count, node.Keys.LastFrame);
    }

    /// <summary>シーンのセルフ影のキーを、VMD のセルフ影のキーにする（30 fps に直す。段階なのでそのまま）。</summary>
    public static IReadOnlyList<VmdShadowKey> CreateKeys(SelfShadowNode node, int frameRate, LoadReport report)
    {
        if (node.Keys.Count == 0)
        {
            return [];
        }
        double scale = MotionImport.MmdFrameRate / (double)frameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        var written = new SortedDictionary<int, VmdShadowKey>();
        int overlapped = 0;
        foreach (SelfShadowKey key in node.Keys.Keys)
        {
            int frame = Scale(key.Frame);
            if (written.ContainsKey(frame))
            {
                overlapped++;
            }
            written[frame] = new VmdShadowKey(frame, key.Mode, key.Distance);
        }
        if (scale != 1)
        {
            InfoOnce(report, $"作品のフレームの速さ（{frameRate} fps）から、MMD の 30 fps に直してキーの位置を {scale:0.##} 倍にしました");
        }
        if (overlapped > 0)
        {
            report.Warning(LoadIssueCategory.Note, $"30 fps に直したときに同じフレームに重なったセルフ影のキーが {overlapped:N0} 件あり、後ろのキーを使いました");
        }
        if (node.HasUnregisteredChange)
        {
            report.Warning(LoadIssueCategory.Note, "セルフ影にキーに登録していない変更があり、書き出していません（書き出すには先に登録してください）");
        }
        return [.. written.Values];
    }

    private static void InfoOnce(LoadReport report, string message)
    {
        if (!report.Issues.Any(i => i.Message == message))
        {
            report.Info(LoadIssueCategory.Note, message);
        }
    }
}
