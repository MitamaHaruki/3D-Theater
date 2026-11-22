using System.Numerics;

namespace Theater3D.Tracking;

/// <summary>
/// 手のトラッキングの手を、体の点の手首と結び付ける（3D Theater VCamera。設計判断記録 100 章）。
/// 手の左右は、手の形だけから見分けるので、ときどき取り違える。体のモデルの左右は、顔や肩から見分けるので取り違えにくい。
/// そこで、手の手首（画面の位置）が、体のどちらかの手首のはっきり近くにあれば、その側の手とする（左右の答えを直す）。
/// あわせて、その側の体の手首を「見えている」とする（手を顔の近くへ上げると、体のモデルの手首の見えている度合いが下がるため）。
/// どちらの手首にもはっきり近くなければ（見えていない手首が、見えている手の近くに見立てられたときなど）、何もしない。
/// </summary>
public static class BodyHandMatch
{
    /// <summary>手の手首が、体の手首からこの距離（肩幅に対して）より近ければ、結び付ける候補。</summary>
    public const float NearShoulderWidths = 0.6f;

    /// <summary>近いほうの体の手首までが、遠いほうまでのこの割合より近ければ、はっきり近いとみなす。</summary>
    public const float ClearRatio = 0.6f;

    /// <summary>手と結び付けた側の、体の手首の見えている度合い（これより低ければ、ここまで上げる）。</summary>
    public const float SeenVisibility = 0.9f;

    /// <summary>手の左右と、体の手首の見えている度合いを直した結果を返す（直すことが無ければ、渡したもののまま）。</summary>
    public static (HandTrackingResult Hands, PoseTrackingResult Body) Apply(HandTrackingResult hands, PoseTrackingResult body)
    {
        ArgumentNullException.ThrowIfNull(hands);
        ArgumentNullException.ThrowIfNull(body);
        if (!body.Found || body.Body is not { } pose || body.Landmarks.Length < BodyPose.PointCount || hands.Hands.Count == 0)
        {
            return (hands, body);
        }
        static Vector2 Xy(Vector3 p) => new(p.X, p.Y);
        Vector2 left = Xy(body.Landmarks[BodyPose.LeftWrist]);
        Vector2 right = Xy(body.Landmarks[BodyPose.RightWrist]);
        float shoulders = Vector2.Distance(Xy(body.Landmarks[BodyPose.LeftShoulder]), Xy(body.Landmarks[BodyPose.RightShoulder]));
        if (!(shoulders > 1f))
        {
            return (hands, body);
        }
        // 手ごとに、はっきり近い体の手首の側（0 = 本人の左・1 = 右。決まらなければ -1）
        var side = new int[hands.Hands.Count];
        var distance = new float[hands.Hands.Count];
        for (int i = 0; i < side.Length; i++)
        {
            Vector2 wrist = Xy(hands.Hands[i].Landmarks[0]);
            float toLeft = Vector2.Distance(wrist, left);
            float toRight = Vector2.Distance(wrist, right);
            float near = Math.Min(toLeft, toRight);
            side[i] = near <= NearShoulderWidths * shoulders && near <= ClearRatio * Math.Max(toLeft, toRight) ? (toLeft < toRight ? 0 : 1) : -1;
            distance[i] = near;
        }
        // 2 つの手が同じ側になったら、近いほうだけ結び付ける
        if (side.Length == 2 && side[0] >= 0 && side[0] == side[1])
        {
            side[distance[0] <= distance[1] ? 1 : 0] = -1;
        }
        if (side.All(s => s < 0))
        {
            return (hands, body);
        }

        var result = hands.Hands.ToList();
        for (int i = 0; i < result.Count; i++)
        {
            if (side[i] < 0 || result[i].IsRight == (side[i] == 1))
            {
                continue;
            }
            bool isRight = side[i] == 1;
            result[i] = result[i] with { IsRight = isRight, Handedness = 1f - result[i].Handedness };
            // もう一方の手が同じ側の答えなら、反対の側にする（2 つの手は別々の側）
            int other = 1 - i;
            if (result.Count == 2 && side[other] < 0 && result[other].IsRight == isRight)
            {
                result[other] = result[other] with { IsRight = !isRight, Handedness = 1f - result[other].Handedness };
            }
        }
        BodyPose seen = new([.. pose.Points], [.. pose.Visibility]);
        for (int i = 0; i < side.Length; i++)
        {
            if (side[i] >= 0)
            {
                int wrist = side[i] == 1 ? BodyPose.RightWrist : BodyPose.LeftWrist;
                seen.Visibility[wrist] = Math.Max(seen.Visibility[wrist], SeenVisibility);
            }
        }
        return (hands with { Hands = result }, body with { Body = seen });
    }
}
