using System.Numerics;

namespace Theater3D.Tracking.Vmc;

/// <summary>
/// 顔のトラッキングの結果を、送る 1 枚分（<see cref="VmcFaceFrame"/>）にする（3D Theater VCamera。設計判断記録 98 章）。
/// ほかのアプリ向けの頭のボーンの回転は、正面に合わせた後の向き（覚えた正面からの回り方）にする。正面は、顔を見つけてから
/// <see cref="CalibrationFrames"/> 枚の平均で覚え、<see cref="Recalibrate"/> で覚え直す。
/// 3D Theater 向けの値（向き・真ん中・大きさ）は合わせる前のまま送る（本アプリが、カメラのときと同じに正面を合わせる）。
/// </summary>
public sealed class VmcFaceEncoder
{
    /// <summary>正面を覚えるのに使う枚数（本アプリのカメラのトラッキングと同じ）。</summary>
    public const int CalibrationFrames = 15;

    private readonly List<Quaternion> _calibration = [];
    private Quaternion? _neutral;
    private int _sequence;

    /// <summary>正面を覚えたか。</summary>
    public bool IsCalibrated => _neutral is not null;

    /// <summary>正面を覚え直す（次に顔を見つけた枚から）。</summary>
    public void Recalibrate()
    {
        _neutral = null;
        _calibration.Clear();
    }

    /// <summary>
    /// 1 枚分を作る。<paramref name="processMilliseconds"/> は絵が届いてから送るまでの時間、<paramref name="time"/> は始めてからの秒。
    /// 顔が無い枚は、顔なしとして送る（ボーンと表情は送らない）。
    /// </summary>
    public VmcFaceFrame Encode(FaceTrackingResult result, float processMilliseconds, float time)
    {
        ArgumentNullException.ThrowIfNull(result);
        int sequence = _sequence++;
        if (!result.Found)
        {
            return new VmcFaceFrame(sequence, false, 0f, 0f, 0f, Vector2.Zero, 0f, new Dictionary<string, float>(), Quaternion.Identity, processMilliseconds, time);
        }
        HeadPose pose = result.Pose;
        Quaternion current = VmcProtocol.HeadRotation(pose.Yaw, pose.Pitch, pose.Roll);
        if (_neutral is null)
        {
            _calibration.Add(current);
            if (_calibration.Count >= CalibrationFrames)
            {
                _neutral = Average(_calibration);
                _calibration.Clear();
            }
        }
        // 正面からの回り方（正面を覚えるまでは、まっすぐ前とみなす）
        Quaternion head = _neutral is { } neutral ? Quaternion.Normalize(current * Quaternion.Inverse(neutral)) : Quaternion.Identity;
        return new VmcFaceFrame(sequence, true, pose.Yaw, pose.Pitch, pose.Roll, result.Center, result.Size, result.BlendshapeMap(), head,
            processMilliseconds, time);
    }

    // 回転の平均（近い回転どうしなので、向きをそろえて足して長さ 1 にする）
    private static Quaternion Average(List<Quaternion> rotations)
    {
        Vector4 sum = Vector4.Zero;
        Quaternion first = rotations[0];
        foreach (Quaternion q in rotations)
        {
            var v = new Vector4(q.X, q.Y, q.Z, q.W);
            sum += Quaternion.Dot(q, first) < 0 ? -v : v;
        }
        return Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
    }
}
