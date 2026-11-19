using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor;

/// <summary>
/// VMD のカメラのキーと、シーンのカメラのキーの変換（ここ 1 か所にまとめる）。
/// MMD のカメラは「注視点・回転（ラジアン）・距離（負の数で注視点の手前）・視野角」で、
/// カメラの位置 = 注視点 + 回転 × (0, 0, 距離)。回転は Z → X → Y の順に掛ける（saba と同じ）。
/// シーンのカメラの回転（度、X が正なら見下ろす）とは、X と Y の向きが逆になる（saba・three.js の扱いに合わせた。
/// MMD 本体との見比べはまだ）。補間の曲線は 6 本（注視点 X・Y・Z、回転、距離、視野角）を各 4 バイト（x1, x2, y1, y2）で持つ。
/// </summary>
public static class MmdCamera
{
    private const float ToDegrees = 180f / MathF.PI;
    private const float ToRadians = MathF.PI / 180f;

    public static CameraKey FromVmd(VmdCameraKey key, int frame) => new(
        frame,
        key.Target,
        new Vector3(-key.Rotation.X, -key.Rotation.Y, key.Rotation.Z) * ToDegrees,
        -key.Distance,
        key.FieldOfView,
        Interpolation(key.Interpolation),
        KeyInterpolationKind.Curve,
        key.Orthographic);

    public static VmdCameraKey ToVmd(CameraKey key, int frame) => new(
        frame,
        -key.Distance,
        key.Target,
        new Vector3(-key.Rotation.X, -key.Rotation.Y, key.Rotation.Z) * ToRadians,
        key.Kind == KeyInterpolationKind.Step ? Bytes(CameraInterpolation.Linear) : Bytes(key.Interpolation),
        (int)MathF.Round(key.FieldOfView),
        key.Orthographic);

    public static CameraInterpolation Interpolation(byte[] bytes)
    {
        if (bytes.Length < 24)
        {
            return CameraInterpolation.Linear;
        }
        BezierCurve Curve(int p) => BezierCurve.FromMmd(bytes[(p * 4) + 0], bytes[(p * 4) + 2], bytes[(p * 4) + 1], bytes[(p * 4) + 3]);
        // すべて 0 の曲線（古い書き出し器）は直線として扱う
        return bytes.All(b => b == 0)
            ? CameraInterpolation.Linear
            : new CameraInterpolation(Curve(0), Curve(1), Curve(2), Curve(3), Curve(4), Curve(5));
    }

    public static byte[] Bytes(CameraInterpolation ip)
    {
        var bytes = new byte[24];
        BezierCurve[] curves = [ip.X, ip.Y, ip.Z, ip.Rotation, ip.Distance, ip.FieldOfView];
        for (int p = 0; p < 6; p++)
        {
            bytes[(p * 4) + 0] = ToByte(curves[p].Control1.X);
            bytes[(p * 4) + 1] = ToByte(curves[p].Control2.X);
            bytes[(p * 4) + 2] = ToByte(curves[p].Control1.Y);
            bytes[(p * 4) + 3] = ToByte(curves[p].Control2.Y);
        }
        return bytes;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 127f), 0, 127);
}

/// <summary>カメラのモーションを当てはめた結果。</summary>
public sealed record CameraImportResult(int Keys, int LastFrame);

public static class CameraMotion
{
    /// <summary>
    /// VMD のカメラのキーを、シーンのカメラへ足す（同じフレームのキーは置き換える）。30 fps 以外の作品では位置を合わせ、
    /// 元で 1 フレームしか離れていないキー（カメラの切り替え）は、間に途中の絵が出ないよう「段階」にする。
    /// </summary>
    public static CameraImportResult Apply(VmdDocument vmd, CameraNode camera, int frameRate, LoadReport report)
    {
        double scale = frameRate / (double)MotionImport.MmdFrameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        var keys = new List<CameraKey>();
        int cuts = 0;
        VmdCameraKey? previous = null;
        foreach (VmdCameraKey k in vmd.CameraKeys.OrderBy(k => k.Frame))
        {
            CameraKey key = MmdCamera.FromVmd(k, Scale(k.Frame));
            if (scale > 1 && previous is { } p && k.Frame - p.Frame == 1)
            {
                key = key with { Kind = KeyInterpolationKind.Step };
                cuts++;
            }
            keys.Add(key);
            previous = k;
        }
        camera.Keys.SetRange(keys);

        if (scale != 1)
        {
            report.Info(LoadIssueCategory.Note, $"作品のフレームの速さ（{frameRate} fps）に合わせて、キーの位置を {scale:0.##} 倍にしました（MMD のモーションは 30 fps）");
        }
        if (cuts > 0)
        {
            report.Info(LoadIssueCategory.Note, $"1 フレームで切り替わるカメラ（{cuts:N0} か所）は、途中の絵が出ないよう段階の補間にしました");
        }
        if (vmd.BoneKeys.Count > 0 || vmd.MorphKeys.Count > 0)
        {
            report.Info(LoadIssueCategory.Note, "ボーン・モーフのキーは、カメラには当てはめません（モデルを選んで読み込むと当てはまります）");
        }
        if (keys.Count == 0)
        {
            report.Warning(LoadIssueCategory.Note, "カメラのキーがありません");
        }
        return new CameraImportResult(keys.Count, camera.Keys.LastFrame);
    }

    /// <summary>
    /// シーンのカメラのキーを VMD（カメラ・照明用）にする。30 fps に直し、段階の補間は 1 つ前のフレームにキーを足して表す。
    /// <paramref name="light"/>（MMD の照明）・<paramref name="selfShadow"/> にキーがあれば、照明・セルフ影のキーも一緒に書く
    /// （<see cref="LightMotion.CreateKeys"/>・<see cref="SelfShadowMotion.CreateKeys"/>）。<paramref name="time"/>（時刻の項目）にキーがあれば、
    /// 照明のキーは、時刻で動くサンライトから作る（<see cref="LightMotion.CreateKeysFromTime"/>）。
    /// </summary>
    public static VmdDocument CreateVmd(CameraNode camera, int frameRate, LoadReport report, LightNode? light = null, SelfShadowNode? selfShadow = null,
        TimeOfDayNode? time = null)
    {
        double scale = MotionImport.MmdFrameRate / (double)frameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        var written = new SortedDictionary<int, VmdCameraKey>();
        int steps = 0, overlapped = 0;
        CameraKey? previous = null;
        foreach (CameraKey key in camera.Keys.Keys)
        {
            int frame = Scale(key.Frame);
            if (key.Kind == KeyInterpolationKind.Step && previous is { } p && frame - 1 > Scale(p.Frame))
            {
                written[frame - 1] = MmdCamera.ToVmd(p with { Kind = KeyInterpolationKind.Curve, Interpolation = CameraInterpolation.Linear }, frame - 1);
                steps++;
            }
            if (written.ContainsKey(frame))
            {
                overlapped++;
            }
            written[frame] = MmdCamera.ToVmd(key, frame);
            previous = key;
        }
        if (scale != 1)
        {
            report.Info(LoadIssueCategory.Note, $"作品のフレームの速さ（{frameRate} fps）から、MMD の 30 fps に直してキーの位置を {scale:0.##} 倍にしました");
        }
        if (overlapped > 0)
        {
            report.Warning(LoadIssueCategory.Note, $"30 fps に直したときに同じフレームに重なったキーが {overlapped:N0} 件あり、後ろのキーを使いました");
        }
        if (steps > 0)
        {
            report.Info(LoadIssueCategory.Note, $"段階の補間（{steps:N0} 件）は VMD に無いため、1 つ前のフレームにキーを足して表しました");
        }
        if (camera.HasUnregisteredChange)
        {
            report.Warning(LoadIssueCategory.Note, "カメラにキーに登録していない変更があり、書き出していません（書き出すには先に登録してください）");
        }
        IReadOnlyList<VmdLightKey> lights = light is null ? []
            : time is not null ? LightMotion.CreateKeysFromTime(time, light, frameRate, report)
            : LightMotion.CreateKeys(light, frameRate, report);
        IReadOnlyList<VmdShadowKey> shadows = selfShadow is null ? [] : SelfShadowMotion.CreateKeys(selfShadow, frameRate, report);
        if (written.Count == 0 && lights.Count == 0 && shadows.Count == 0)
        {
            report.Warning(LoadIssueCategory.Note, light is null ? "カメラのキーがありません（空のモーションになります）" : "カメラ・照明のキーがありません（空のモーションになります）");
        }
        return new VmdDocument
        {
            ModelName = VmdName.FromText("カメラ・照明", VmdText.ModelNameBytes),
            CameraKeys = [.. written.Values],
            LightKeys = lights,
            ShadowKeys = shadows,
        };
    }
}
