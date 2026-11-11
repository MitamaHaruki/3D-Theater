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
