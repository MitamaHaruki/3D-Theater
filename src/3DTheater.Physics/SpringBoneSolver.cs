using System.Numerics;
using Theater3D.Animation;
using Theater3D.Formats.Pmx;

namespace Theater3D.Physics;

/// <summary>
/// VRM の揺れ物（スプリングボーン）の計算（設計判断記録 82 章の 4 段目）。VRM の決まりと同じく、ボーンの先端の位置を 1 歩ずつ動かす:
/// <code>
/// 次の先端 = 今の先端 + (今の先端 - 前の先端) × (1 - dragForce)          … 動きの勢い（空気の抵抗で弱める）
///          + 元の向き × stiffness × 時間                                  … 元の向き（親とモーションで決まる向き）へ戻る
///          + 重力の向き × gravityPower × 時間                              … 重力
/// </code>
/// それを、ボーンの長さに戻し（根元からの距離をそろえる）、当たる物（球・カプセル）にめり込めば押し出して、ボーンをその先端の方へ回す。
/// 強さと重力は VRM の 1 m を 1 とした値なので、<see cref="SpringBoneSet.LengthScale"/> を掛ける。根元から順に計算する（親を揺らした後の位置から子を計算する）。
/// 結果はボーンの置き方として、物理演算の剛体の置き方と同じ並びに入れる（<see cref="ModelPhysics"/> が、焼き込み・やり直しで一緒に扱う）。
/// </summary>
public sealed class SpringBoneSolver
{
    /// <summary>1 歩の時間（秒）。VRM は 1 フレームに 1 歩なので、1/60 秒ずつに分けて、描く速さが変わっても同じ揺れにする。</summary>
    public const float StepSeconds = 1f / 60f;

    /// <summary>1 回に進める歩数の上限（フレームを大きく飛ばしたときに、計算が重くならないように）。</summary>
    public const int MaxSteps = 8;

    private readonly ModelRig _rig;
    private readonly Joint[] _joints;
    private readonly Collider[] _colliders;
    private readonly Vector3[] _current;
    private readonly Vector3[] _previous;
    private readonly Matrix4x4[] _results; // 最後に置いた、揺れるボーンの置き方（焼き込みで写す）
    private readonly float _scale;
    private readonly bool _hasGravity;

    private readonly record struct Joint(int Bone, Vector3 TailOffset, float Length, float Stiffness, float GravityPower, Vector3 GravityDirection,
        float Drag, float HitRadius, int[] Colliders);

    private readonly record struct Collider(int Bone, Vector3 CenterOffset, Vector3? TailOffset, float Radius);

    private SpringBoneSolver(ModelRig rig, Joint[] joints, Collider[] colliders, float scale)
    {
        _rig = rig;
        _joints = joints;
        _colliders = colliders;
        _scale = scale;
        _current = new Vector3[joints.Length];
        _previous = new Vector3[joints.Length];
        _results = new Matrix4x4[joints.Length];
        _hasGravity = joints.Any(j => j.GravityPower > 0);
    }

    /// <summary>揺れるボーンの数（物理演算の剛体の置き方の並びに、この数だけ足す）。</summary>
    public int JointCount => _joints.Length;

    /// <summary>VRM の揺れ物の設定から作る。設定が無いか、使えるボーンが 1 本も無ければ null。</summary>
    public static SpringBoneSolver? Create(SpringBoneSet? set, ModelRig rig)
    {
        if (set is null || set.Joints.Count == 0)
        {
            return null;
        }
        int boneCount = rig.Bones.Length;
        // ボーンの初めの位置からの差（ボーンの置き方で動かすと、今の位置になる）
        Collider[] colliders = [.. set.Colliders.Select(c => (uint)c.Bone < (uint)boneCount
            ? new Collider(c.Bone, c.Center - rig.Bones[c.Bone].RestPosition, c.Tail - rig.Bones[c.Bone].RestPosition, Math.Max(0f, c.Radius))
            : new Collider(-1, Vector3.Zero, null, 0f))];
        // 親が先の順に並べる（親を揺らした後の位置から、子を計算するため）
        int[] rank = new int[boneCount];
        for (int k = 0; k < rig.HierarchyOrder.Length; k++)
        {
            rank[rig.HierarchyOrder[k]] = k;
        }
        Joint[] joints = [.. set.Joints
            .Where(j => (uint)j.Bone < (uint)boneCount)
            .GroupBy(j => j.Bone)
            .Select(g => g.First()) // 同じボーンを 2 つの揺れ物が指していれば、先のものを使う
            .OrderBy(j => rank[j.Bone])
            .Select(j =>
            {
                Vector3 offset = j.Tail - rig.Bones[j.Bone].RestPosition;
                return new Joint(j.Bone, offset, offset.Length(), Math.Max(0f, j.Stiffness), Math.Max(0f, j.GravityPower),
                    j.GravityDirection.LengthSquared() > 1e-12f ? Vector3.Normalize(j.GravityDirection) : -Vector3.UnitY, Math.Clamp(j.DragForce, 0f, 1f),
                    Math.Max(0f, j.HitRadius), [.. j.Colliders.Where(c => (uint)c < (uint)colliders.Length && colliders[c].Bone >= 0)]);
            })
            .Where(j => j.Length > 1e-6f)];
        return joints.Length == 0 ? null : new SpringBoneSolver(rig, joints, colliders, set.LengthScale > 0 ? set.LengthScale : 1f);
    }

    /// <summary>
    /// 先端を今のボーンの置き方（揺れていない向き）に置き直し、動きの勢いを 0 にする（読み込んだ直後・フレームを飛ばしたとき）。
    /// 重力で垂れる揺れ物があれば、<paramref name="warmupSeconds"/> 秒だけボーンを止めたまま計算して落ち着かせる。
    /// </summary>
    public void Reset(PoseState state, float warmupSeconds)
    {
        for (int i = 0; i < _joints.Length; i++)
        {
            Joint joint = _joints[i];
            PoseEvaluator.RefreshGlobal(_rig, state, joint.Bone);
            _current[i] = _previous[i] = Vector3.Transform(joint.TailOffset, state.Global[joint.Bone]);
            state.SetPhysicsResult(joint.Bone, state.Global[joint.Bone]);
        }
        if (_hasGravity && warmupSeconds > 0)
        {
            int steps = Math.Min((int)MathF.Ceiling(warmupSeconds / StepSeconds), 240);
            for (int s = 0; s < steps; s++)
            {
                Solve(state, StepSeconds, advance: true);
                Array.Copy(_current, _previous, _current.Length); // 落ち着かせる間は勢いを残さない
            }
        }
        Solve(state, 0f, advance: false);
    }

    /// <summary><paramref name="seconds"/> 秒進める（1/60 秒ずつ。<see cref="MaxSteps"/> 歩まで）。</summary>
    public void Step(PoseState state, float seconds)
    {
        if (!(seconds > 0))
        {
            Solve(state, 0f, advance: false);
            return;
        }
        int steps = Math.Clamp((int)MathF.Ceiling((seconds / StepSeconds) - 1e-3f), 1, MaxSteps);
        float dt = Math.Min(seconds / steps, StepSeconds * 2f);
        for (int s = 0; s < steps; s++)
        {
            Solve(state, dt, advance: true);
        }
    }

    /// <summary>今の先端の位置で、ボーンを置く（計算は進めない）。</summary>
    public void Apply(PoseState state) => Solve(state, 0f, advance: false);

    /// <summary>最後に置いた揺れるボーンの置き方を写す（焼き込みで覚えておくため。<see cref="JointCount"/> だけ要る）。</summary>
    public void CopyTransforms(Span<PhysicsTransform> destination)
    {
        for (int i = 0; i < _joints.Length && i < destination.Length; i++)
        {
            destination[i] = PhysicsTransform.FromMatrix(_results[i]);
        }
    }

    /// <summary>与えた置き方（焼き込みで覚えたものなど）で、揺れるボーンを置く。</summary>
    public void ApplyTransforms(PoseState state, ReadOnlySpan<PhysicsTransform> transforms)
    {
        for (int i = 0; i < _joints.Length && i < transforms.Length; i++)
        {
            _results[i] = transforms[i].ToMatrix();
            state.SetPhysicsResult(_joints[i].Bone, _results[i]);
        }
    }

    // 根元から順に、先端を動かして（advance）、ボーンをその方へ回す
    private void Solve(PoseState state, float dt, bool advance)
    {
        for (int i = 0; i < _joints.Length; i++)
        {
            Joint joint = _joints[i];
            // 揺れていない置き方（親の今の置き方と、このボーンのモーション）から始める
            PoseEvaluator.RefreshGlobal(_rig, state, joint.Bone);
            Matrix4x4 global = state.Global[joint.Bone];
            Vector3 head = global.Translation;
            Vector3 rest = Vector3.Transform(joint.TailOffset, global);
            Vector3 next = _current[i];
            if (advance)
            {
                Vector3 restDirection = Vector3.Normalize(rest - head);
                next = _current[i]
                    + (_current[i] - _previous[i]) * (1f - joint.Drag)
                    + restDirection * (joint.Stiffness * dt * _scale)
                    + joint.GravityDirection * (joint.GravityPower * dt * _scale);
            }
            next = Constrain(head, next, joint.Length, rest);
            next = Collide(state, joint, head, next);
            if (advance)
            {
                _previous[i] = _current[i];
                _current[i] = next;
            }
            // ボーンを、揺れていない先端の向きから、揺れた先端の向きへ回す（根元を中心に）
            Quaternion turn = FromTo(rest - head, next - head);
            Matrix4x4 swung = global * Matrix4x4.CreateTranslation(-head) * Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(head);
            _results[i] = swung;
            state.SetPhysicsResult(joint.Bone, swung);
        }
        PoseEvaluator.RefreshPhysicsChildren(_rig, state);
    }

    // 根元からの距離を、ボーンの長さにそろえる（根元に重なったら、揺れていない向きに置く）
    private static Vector3 Constrain(Vector3 head, Vector3 tail, float length, Vector3 rest)
    {
        Vector3 along = tail - head;
        float distance = along.Length();
        return distance > 1e-6f && float.IsFinite(distance) ? head + along * (length / distance) : rest;
    }

    // 当たる物（球・カプセル）にめり込んだら、外へ押し出して、長さをそろえ直す
    private Vector3 Collide(PoseState state, Joint joint, Vector3 head, Vector3 tail)
    {
        foreach (int index in joint.Colliders)
        {
            Collider c = _colliders[index];
            Matrix4x4 placement = state.Global[c.Bone];
            Vector3 center = Vector3.Transform(c.CenterOffset, placement);
            Vector3 nearest = center;
            if (c.TailOffset is { } tailOffset)
            {
                Vector3 end = Vector3.Transform(tailOffset, placement);
                Vector3 segment = end - center;
                float t = segment.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(tail - center, segment) / segment.LengthSquared(), 0f, 1f) : 0f;
                nearest = center + segment * t;
            }
            float reach = c.Radius + joint.HitRadius;
            Vector3 away = tail - nearest;
            float distance = away.Length();
            if (distance < reach)
            {
                tail = distance > 1e-6f ? nearest + away * (reach / distance) : nearest + Vector3.UnitY * reach;
                tail = Constrain(head, tail, joint.Length, tail);
            }
        }
        return tail;
    }

    // from の向きを to の向きへ回す、いちばん小さい回転
    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f)
        {
            return Quaternion.Identity;
        }
        Vector3 a = Vector3.Normalize(from);
        Vector3 b = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 1f - 1e-7f)
        {
            return Quaternion.Identity;
        }
        if (dot < -1f + 1e-6f)
        {
            Vector3 axis = Vector3.Cross(a, Vector3.UnitX);
            if (axis.LengthSquared() < 1e-6f)
            {
                axis = Vector3.Cross(a, Vector3.UnitY);
            }
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 cross = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
    }
}
