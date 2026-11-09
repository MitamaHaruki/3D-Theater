using System.Numerics;
using Theater3D.Animation;
using Theater3D.Formats;
using Theater3D.Formats.Pmx;

namespace Theater3D.Physics;

/// <summary>
/// モデル 1 体の物理演算（PMX の剛体とジョイント）。物理の世界はモデルごとに 1 つで、モデルの座標で計算する。
/// 使い方: <see cref="PoseEvaluator.EvaluateBeforePhysics"/> の後に <see cref="Reset"/> か <see cref="Step"/> か <see cref="Apply"/> を呼び、
/// その後で <see cref="PoseEvaluator.EvaluateAfterPhysics"/> を呼ぶ。
/// 剛体の種類（PMX）: ボーン追従 = ボーンの置き方に合わせて外から動かす。物理演算 = 物理の結果でボーンを置く。
/// 物理＋ボーン位置合わせ = 物理の結果の向きだけを使い、ボーンの位置は動かさない。
/// </summary>
public sealed class ModelPhysics : IDisposable
{
    /// <summary>計算を細かく分ける間（秒）。1 フレームを 1/120 秒ずつに分けて進める。</summary>
    public const float FixedStep = 1f / 120f;

    /// <summary>重力の大きさ（9.8 × 10。saba・three.js の MMD の物理と同じ値）。</summary>
    public const float GravityStrength = 98f;

    /// <summary>フレームを飛ばしたときに、動かさずに落ち着かせる時間（秒）。</summary>
    public const float DefaultWarmup = 1f;

    private readonly PhysicsWorld _world;
    private readonly ModelRig _rig;
    private readonly Body[] _bodies;
    private readonly PhysicsTransform[] _buffer;
    private readonly RigMorph[] _impulseMorphs;
    private readonly ImpulseSum[] _impulseSums;
    private readonly int[] _impulseBodies;
    private readonly PhysicsImpulse[] _impulses;

    private ModelPhysics(PhysicsWorld world, ModelRig rig, Body[] bodies, int joints)
    {
        _world = world;
        _rig = rig;
        _bodies = bodies;
        _buffer = new PhysicsTransform[bodies.Length];
        JointCount = joints;
        _impulseMorphs = [.. rig.Morphs.Where(m => m.ImpulseOffsets.Length > 0)];
        _impulseSums = new ImpulseSum[bodies.Length];
        _impulseBodies = new int[bodies.Length];
        _impulses = new PhysicsImpulse[bodies.Length * 2];
    }

    public int BodyCount => _bodies.Length;

    /// <summary>剛体を押すインパルスモーフ（PMX 2.1）があるか。</summary>
    public bool HasImpulseMorphs => _impulseMorphs.Length > 0;

    public int JointCount { get; }

    /// <summary>物理で動く剛体の数（ボーン追従でないもの）。</summary>
    public int DynamicBodyCount => _bodies.Count(b => !b.Kinematic);

    /// <summary>次の計算で置き直しが要る（読み込んだ直後・物理を入れ直したとき）。</summary>
    public bool NeedsReset { get; set; } = true;

    /// <summary>
    /// PMX の剛体とジョイントから作る。剛体が無ければ null。使えないジョイントの種類などは <paramref name="report"/> で知らせる。
    /// </summary>
    public static ModelPhysics? Create(PmxDocument document, ModelRig rig, LoadReport? report = null)
    {
        if (document.RigidBodies.Length == 0)
        {
            return null;
        }
        int boneCount = rig.Bones.Length;
        var bodies = new Body[document.RigidBodies.Length];
        var descs = new PhysicsBodyDesc[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            PmxRigidBody r = document.RigidBodies[i];
            int bone = (uint)r.BoneIndex < (uint)boneCount ? r.BoneIndex : -1;
            Matrix4x4 rest = Transform(r.Position, r.Rotation);
            // 剛体からボーンへの関係: 剛体の置き方 = Offset × ボーンの置き方（ボーンが無ければモデルの原点に付く）
            Matrix4x4 offset = bone >= 0 ? rest * Matrix4x4.CreateTranslation(-rig.Bones[bone].RestPosition) : rest;
            Matrix4x4.Invert(offset, out Matrix4x4 inverse);
            bool kinematic = r.Mode == PmxPhysicsMode.FollowBone;
            bodies[i] = new Body(bone, r.Mode, kinematic, offset, inverse);
            descs[i] = new PhysicsBodyDesc
            {
                Shape = r.Shape switch
                {
                    PmxRigidShape.Box => PhysicsShape.Box,
                    PmxRigidShape.Capsule => PhysicsShape.Capsule,
                    _ => PhysicsShape.Sphere,
                },
                Kind = kinematic ? PhysicsBodyKind.Kinematic : PhysicsBodyKind.Dynamic,
                Size = Finite(r.Size),
                Mass = float.IsFinite(r.Mass) ? r.Mass : 1f,
                Transform = PhysicsTransform.FromMatrix(rest),
                LinearDamping = Finite(r.LinearDamping),
                AngularDamping = Finite(r.AngularDamping),
                Restitution = Finite(r.Restitution),
                Friction = Finite(r.Friction),
                Group = 1u << (r.Group & 15),
                Mask = r.CollisionMask,
            };
        }

        var joints = new List<PhysicsJointDesc>();
        int unsupported = 0;
        foreach (PmxJoint j in document.Joints)
        {
            if ((uint)j.RigidBodyA >= (uint)bodies.Length || (uint)j.RigidBodyB >= (uint)bodies.Length || j.RigidBodyA == j.RigidBodyB)
            {
                continue; // 壊れた参照は読み込みレポートで知らせ済み
            }
            if (j.Type is not (PmxJointType.Spring6Dof or PmxJointType.SixDof))
            {
                unsupported++;
                continue;
            }
            bool springs = j.Type == PmxJointType.Spring6Dof;
            joints.Add(new PhysicsJointDesc
            {
                BodyA = j.RigidBodyA,
                BodyB = j.RigidBodyB,
                Transform = PhysicsTransform.FromMatrix(Transform(j.Position, j.Rotation)),
                LinearLower = Finite(j.PositionMin),
                LinearUpper = Finite(j.PositionMax),
                AngularLower = Finite(j.RotationMin),
                AngularUpper = Finite(j.RotationMax),
                LinearStiffness = springs ? Finite(j.PositionSpring) : Vector3.Zero,
                AngularStiffness = springs ? Finite(j.RotationSpring) : Vector3.Zero,
            });
        }
        if (unsupported > 0)
        {
            report?.Warning(LoadIssueCategory.Unsupported,
                $"PMX 2.1 のジョイント（点・円錐・スライダー・蝶番）{unsupported:N0} 個には、まだ対応していないため使いません");
        }

        PhysicsWorld world = PhysicsWorld.Create(new Vector3(0, -GravityStrength, 0));
        try
        {
            world.AddBodies(descs);
            world.AddJoints(joints.ToArray()); // ジョイントは、剛体を初期の置き方で足した直後に作る
        }
        catch
        {
            world.Dispose();
            throw;
        }
        return new ModelPhysics(world, rig, bodies, joints.Count);
    }

    /// <summary>重力（モデルの座標で）。モデルを傾けて置いたときは、傾きに合わせて回したものを渡す。</summary>
    public void SetGravity(Vector3 gravity) => _world.SetGravity(gravity);

    /// <summary>
    /// 全剛体を今のボーンの置き方に合わせて置き直し、速さを 0 にしてから、<paramref name="warmupSeconds"/> 秒だけ
    /// ボーンを止めたまま計算して落ち着かせる（フレームを飛ばしたとき・最初）。
    /// </summary>
    public void Reset(PoseState state, float warmupSeconds = DefaultWarmup)
    {
        for (int i = 0; i < _bodies.Length; i++)
        {
            _buffer[i] = PhysicsTransform.FromMatrix(BodyFromBone(state, i));
        }
        _world.Reset(_buffer);
        if (warmupSeconds > 0)
        {
            _world.Step(_buffer, warmupSeconds, Substeps(warmupSeconds));
        }
        NeedsReset = false;
        Apply(state);
    }

    /// <summary>
    /// <paramref name="seconds"/> 秒進める。ボーン追従の剛体は、前の置き方から今のボーンの置き方まで少しずつ動かす。
    /// インパルスモーフ（<see cref="PoseState.EffectiveMorphWeights"/> の値）の勢いは、進める前に
    /// 「1 フレームにつき 1 回」として <paramref name="impulseFrames"/> 回分を与える（何フレームか続けて進めるときはその数）。
    /// </summary>
    public void Step(PoseState state, float seconds, float impulseFrames = 1f)
    {
        if (NeedsReset)
        {
            Reset(state);
            return;
        }
        for (int i = 0; i < _bodies.Length; i++)
        {
            _buffer[i] = _bodies[i].Kinematic ? PhysicsTransform.FromMatrix(BodyFromBone(state, i)) : default;
        }
        if (seconds > 0)
        {
            ApplyImpulseMorphs(state, impulseFrames);
            _world.Step(_buffer, seconds, Substeps(seconds));
        }
        Apply(state);
    }

    /// <summary>
    /// インパルスモーフの勢いを剛体ごとに合わせて与える（PMX 2.1 の決まり）: 値が 0 のモーフは使わない。
    /// 速さと回転の力が両方 0 の項目は「止める」。それ以外は 設定の値 × モーフの値 を、剛体の向きで回すもの（ローカル）と
    /// そのまま使うものに分けて足し、止めてから与える。ボーン追従の剛体には与えない。
    /// </summary>
    private void ApplyImpulseMorphs(PoseState state, float scale)
    {
        if (_impulseMorphs.Length == 0 || !(scale > 0))
        {
            return;
        }
        int touched = 0;
        foreach (RigMorph morph in _impulseMorphs)
        {
            float w = state.EffectiveMorphWeights[morph.Index];
            if (w == 0 || !float.IsFinite(w))
            {
                continue;
            }
            foreach (MorphImpulseOffset o in morph.ImpulseOffsets)
            {
                if (_bodies[o.RigidBody].Kinematic)
                {
                    continue;
                }
                ref ImpulseSum sum = ref _impulseSums[o.RigidBody];
                if (!sum.Used)
                {
                    sum = new ImpulseSum { Used = true };
                    _impulseBodies[touched++] = o.RigidBody;
                }
                if (o.IsStop)
                {
                    sum.Stop = true;
                }
                else if (o.Local)
                {
                    sum.LocalLinear += o.Velocity * (w * scale);
                    sum.LocalTorque += o.Torque * (w * scale);
                }
                else
                {
                    sum.GlobalLinear += o.Velocity * (w * scale);
                    sum.GlobalTorque += o.Torque * (w * scale);
                }
            }
        }

        int count = 0;
        for (int k = 0; k < touched; k++)
        {
            int body = _impulseBodies[k];
            ref ImpulseSum sum = ref _impulseSums[body];
            _impulses[count++] = new PhysicsImpulse
            {
                Body = body,
                Flags = sum.Stop ? PhysicsImpulseFlags.Stop : PhysicsImpulseFlags.None,
                Linear = sum.GlobalLinear,
                Torque = sum.GlobalTorque,
            };
            if (sum.LocalLinear != Vector3.Zero || sum.LocalTorque != Vector3.Zero)
            {
                _impulses[count++] = new PhysicsImpulse { Body = body, Flags = PhysicsImpulseFlags.Local, Linear = sum.LocalLinear, Torque = sum.LocalTorque };
            }
            sum = default;
        }
        _world.ApplyImpulses(_impulses.AsSpan(0, count));
    }

    /// <summary>今の剛体の置き方で、物理演算のボーンを置く（計算は進めない）。</summary>
    public void Apply(PoseState state)
    {
        if (NeedsReset)
        {
            Reset(state);
            return;
        }
        _world.GetTransforms(_buffer);
        ApplyTransforms(state, _buffer);
    }

    /// <summary>今の全剛体の置き方を写す（焼き込みで覚えておくため）。<paramref name="destination"/> は剛体の数だけ要る。</summary>
    public void CopyTransforms(Span<PhysicsTransform> destination) => _world.GetTransforms(destination);

    /// <summary>
    /// 与えた全剛体の置き方（焼き込みで覚えたものなど）で、物理演算のボーンを置く。
    /// <see cref="PoseEvaluator.EvaluateBeforePhysics"/> の後に呼ぶ（物理＋ボーン位置合わせは、そのときのボーンの位置を使う）。
    /// </summary>
    public void ApplyTransforms(PoseState state, ReadOnlySpan<PhysicsTransform> transforms)
    {
        if (transforms.Length != _bodies.Length)
        {
            throw new ArgumentException($"置き方の数（{transforms.Length}）が剛体の数（{_bodies.Length}）と違います", nameof(transforms));
        }
        for (int i = 0; i < _bodies.Length; i++)
        {
            Body body = _bodies[i];
            if (body.Kinematic || body.Bone < 0)
            {
                continue;
            }
            Matrix4x4 global = body.InverseOffset * transforms[i].ToMatrix();
            if (body.Mode == PmxPhysicsMode.PhysicsWithBone)
            {
                global.Translation = state.Global[body.Bone].Translation; // 向きだけ物理に従い、位置はボーンのまま
            }
            state.SetPhysicsResult(body.Bone, global);
        }
        PoseEvaluator.RefreshPhysicsChildren(_rig, state);
    }

    public void Dispose() => _world.Dispose();

    /// <summary>今のボーンの置き方から求めた、剛体の置き方。</summary>
    private Matrix4x4 BodyFromBone(PoseState state, int index)
    {
        Body body = _bodies[index];
        return body.Bone >= 0 ? body.Offset * state.Global[body.Bone] : body.Offset;
    }

    private static int Substeps(float seconds) => Math.Max(1, (int)MathF.Ceiling((seconds / FixedStep) - 1e-3f));

    // PMX の剛体・ジョイントの回転（ラジアン）は、ボーンと同じく Z → X → Y の順に回す
    private static Matrix4x4 Transform(Vector3 position, Vector3 rotation)
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(Finite(rotation.Y), Finite(rotation.X), Finite(rotation.Z)));
        m.Translation = Finite(position);
        return m;
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    private static Vector3 Finite(Vector3 v) => new(Finite(v.X), Finite(v.Y), Finite(v.Z));

    private readonly record struct Body(int Bone, PmxPhysicsMode Mode, bool Kinematic, Matrix4x4 Offset, Matrix4x4 InverseOffset);

    /// <summary>剛体 1 つに与える勢いの合計（インパルスモーフ）。</summary>
    private struct ImpulseSum
    {
        public bool Used;
        public bool Stop;
        public Vector3 LocalLinear;
        public Vector3 LocalTorque;
        public Vector3 GlobalLinear;
        public Vector3 GlobalTorque;
    }
}
