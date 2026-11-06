using System.Numerics;

namespace Theater3D.Animation;

/// <summary>回転の計算の小道具。角度はラジアン。</summary>
public static class Rotations
{
    /// <summary>回転を <paramref name="amount"/> 倍する（0 で回転なし、1 でそのまま、-1 で逆回転、2 で 2 倍）。付与に使う。</summary>
    public static Quaternion Scale(Quaternion rotation, float amount)
    {
        Quaternion q = rotation.W < 0 ? Quaternion.Negate(rotation) : rotation; // 短い向きの回転にそろえる
        float sinHalf = MathF.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z));
        if (sinHalf < 1e-7f)
        {
            return Quaternion.Identity;
        }
        float angle = 2f * MathF.Atan2(sinHalf, q.W);
        var axis = new Vector3(q.X, q.Y, q.Z) / sinHalf;
        return Quaternion.CreateFromAxisAngle(axis, angle * amount);
    }

    /// <summary>回転のうち、<paramref name="axis"/> まわりの成分だけを残す（軸固定のボーン）。</summary>
    public static Quaternion Twist(Quaternion rotation, Vector3 axis)
    {
        Vector3 n = Vector3.Normalize(axis);
        Vector3 projected = Vector3.Dot(new Vector3(rotation.X, rotation.Y, rotation.Z), n) * n;
        var twist = new Quaternion(projected, rotation.W);
        return twist.Length() < 1e-7f ? Quaternion.Identity : Quaternion.Normalize(twist);
    }

    /// <summary>
    /// 回転を X・Y・Z の角度に分ける（回転 = X 軸回転 × Y 軸回転 × Z 軸回転。Z から順に掛かる）。
    /// 答えは 2 通りあるので、<paramref name="previous"/> に近いほうを選ぶ（IK の角度制限で角度が飛ばないように）。
    /// </summary>
    public static Vector3 ToEulerXyz(Quaternion q, Vector3 previous)
    {
        q = Quaternion.Normalize(q);
        float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        float xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        float wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;
        // 列ベクトルの回転行列（R = Rx Ry Rz）の要素
        float r00 = 1 - (2 * (yy + zz));
        float r01 = 2 * (xy - wz);
        float r02 = 2 * (xz + wy);
        float r11 = 1 - (2 * (xx + zz));
        float r12 = 2 * (yz - wx);
        float r21 = 2 * (yz + wx);
        float r22 = 1 - (2 * (xx + yy));

        Vector3 first;
        if (MathF.Abs(r02) > 0.99999f)
        {
            // Y がほぼ ±90 度のときは X と Z を分けられないので、Z を 0 にする
            first = new Vector3(MathF.Atan2(r21, r11), MathF.CopySign(MathF.PI / 2, r02), 0);
            return first;
        }
        first = new Vector3(MathF.Atan2(-r12, r22), MathF.Asin(r02), MathF.Atan2(-r01, r00));
        var second = new Vector3(Wrap(first.X + MathF.PI), Wrap(MathF.PI - first.Y), Wrap(first.Z + MathF.PI));
        return Distance(first, previous) <= Distance(second, previous) ? first : second;
    }

    /// <summary><see cref="ToEulerXyz"/> の逆。</summary>
    public static Quaternion FromEulerXyz(Vector3 angles) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.X)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.Y)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Z);

    /// <summary>シーンの項目と同じ決め（度。Z → X → Y の順）の角度から回転を作る。</summary>
    public static Quaternion FromSceneDegrees(Vector3 degrees)
    {
        Vector3 r = degrees * (MathF.PI / 180f);
        return Quaternion.CreateFromYawPitchRoll(r.Y, r.X, r.Z);
    }

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

    private static float Distance(Vector3 a, Vector3 b) =>
        MathF.Abs(Wrap(a.X - b.X)) + MathF.Abs(Wrap(a.Y - b.Y)) + MathF.Abs(Wrap(a.Z - b.Z));
}
