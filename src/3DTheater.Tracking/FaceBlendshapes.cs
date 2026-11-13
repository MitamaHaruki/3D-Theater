using System.Numerics;

namespace Theater3D.Tracking;

/// <summary>
/// 表情の値（ARKit と同じ名前の 52 個。MediaPipe の face_blendshapes）と、そのモデルに渡す顔の点（478 個のうち 146 個）の決まり。
/// </summary>
public static class FaceBlendshapes
{
    public const int Count = 52;

    /// <summary>表情の値の名前（モデルの出力の順。Left・Right は本人から見た左右）。</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "_neutral", "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight", "cheekPuff",
        "cheekSquintLeft", "cheekSquintRight", "eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight",
        "eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight", "eyeLookUpLeft", "eyeLookUpRight",
        "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight", "jawForward", "jawLeft", "jawOpen", "jawRight",
        "mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight", "mouthFunnel", "mouthLeft",
        "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight", "mouthPucker", "mouthRight",
        "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper", "mouthSmileLeft", "mouthSmileRight",
        "mouthStretchLeft", "mouthStretchRight", "mouthUpperUpLeft", "mouthUpperUpRight", "noseSneerLeft", "noseSneerRight",
    ];

    /// <summary>表情のモデルに渡す顔の点の番号（146 個。瞳の点 468〜477 も含む）。</summary>
    public static IReadOnlyList<int> LandmarkSubset { get; } =
    [
        0, 1, 4, 5, 6, 7, 8, 10, 13, 14, 17, 21, 33, 37, 39,
        40, 46, 52, 53, 54, 55, 58, 61, 63, 65, 66, 67, 70, 78, 80,
        81, 82, 84, 87, 88, 91, 93, 95, 103, 105, 107, 109, 127, 132, 133,
        136, 144, 145, 146, 148, 149, 150, 152, 153, 154, 155, 157, 158, 159, 160,
        161, 162, 163, 168, 172, 173, 176, 178, 181, 185, 191, 195, 197, 234, 246,
        249, 251, 263, 267, 269, 270, 276, 282, 283, 284, 285, 288, 291, 293, 295,
        296, 297, 300, 308, 310, 311, 312, 314, 317, 318, 321, 323, 324, 332, 334,
        336, 338, 356, 361, 362, 365, 373, 374, 375, 377, 378, 379, 380, 381, 382,
        384, 385, 386, 387, 388, 389, 390, 397, 398, 400, 402, 405, 409, 415, 454,
        466, 468, 469, 470, 471, 472, 473, 474, 475, 476, 477,
    ];

    /// <summary>名前から番号（無ければ -1）。</summary>
    public static int IndexOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
        {
            if (Names[i] == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>表情のモデルへの入力（146 個の点の、画像の画素での x・y）を書く。</summary>
    public static void WriteInput(ReadOnlySpan<Vector3> landmarks, Span<float> input)
    {
        for (int i = 0; i < LandmarkSubset.Count; i++)
        {
            Vector3 p = landmarks[LandmarkSubset[i]];
            input[2 * i] = p.X;
            input[(2 * i) + 1] = p.Y;
        }
    }
}

/// <summary>
/// 頭の向き（カメラから見た顔の向き）。顔の点から、顔の右向き（右目尻 → 左目尻）・上向き（あご → 額）・前向きの軸を作って求める。
/// 角度は度。<see cref="Yaw"/> は画像の右を向くと正（本人から見て左）、<see cref="Pitch"/> は上を向くと正、
/// <see cref="Roll"/> は画像の上で時計回りに傾けると正（本人から見て左へ傾ける）。
/// </summary>
public readonly record struct HeadPose(float Yaw, float Pitch, float Roll, Matrix4x4 Rotation)
{
    // 顔の点の番号（MediaPipe の顔の 478 点）
    public const int RightEyeOuter = 33;
    public const int LeftEyeOuter = 263;
    public const int Forehead = 10;
    public const int Chin = 152;
    public const int NoseTip = 1;

    /// <summary>
    /// 画素の座標（x 右・y 下・z 奥。z は x と同じくらいの大きさ）の顔の点から求める。軸はカメラの座標（x 右・y 上・z 手前）で作る。
    /// </summary>
    public static HeadPose FromLandmarks(ReadOnlySpan<Vector3> landmarks)
    {
        static Vector3 Camera(Vector3 p) => new(p.X, -p.Y, -p.Z);
        Vector3 right = Camera(landmarks[LeftEyeOuter]) - Camera(landmarks[RightEyeOuter]);
        Vector3 up = Camera(landmarks[Forehead]) - Camera(landmarks[Chin]);
        Vector3 x = Vector3.Normalize(right);
        Vector3 y = Vector3.Normalize(up - (Vector3.Dot(up, x) * x));
        Vector3 z = Vector3.Cross(x, y); // 顔の前（カメラの方）
        var rotation = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        const float ToDegrees = 180f / MathF.PI;
        float yaw = MathF.Atan2(z.X, z.Z) * ToDegrees;
        float pitch = MathF.Asin(Math.Clamp(z.Y, -1f, 1f)) * ToDegrees;
        float roll = MathF.Atan2(-x.Y, x.X) * ToDegrees;
        return new HeadPose(yaw, pitch, roll, rotation);
    }
}
