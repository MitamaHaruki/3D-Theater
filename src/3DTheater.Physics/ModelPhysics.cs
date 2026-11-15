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
    private readonly PhysicsBodyDesc[] _descs;
    private readonly int[] _followBodies;
    private readonly SpringBoneSolver? _springs; // VRM の揺れ物（剛体の計算の後に計算する。設計判断記録 82 章の 4 段目）
    private PhysicsTransform[] _obstacleTargets = [];

    private ModelPhysics(PhysicsWorld world, ModelRig rig, Body[] bodies, int joints, PhysicsBodyDesc[] descs, SpringBoneSolver? springs)
    {
        _world = world;
        _springs = springs;
        _rig = rig;
        _bodies = bodies;
        _descs = descs;
        _followBodies = [.. Enumerable.Range(0, bodies.Length).Where(i => bodies[i].Kinematic)];
        _buffer = new PhysicsTransform[bodies.Length];
        JointCount = joints;
        _impulseMorphs = [.. rig.Morphs.Where(m => m.ImpulseOffsets.Length > 0)];
        _impulseSums = new ImpulseSum[bodies.Length];
        _impulseBodies = new int[bodies.Length];
        _impulses = new PhysicsImpulse[bodies.Length * 2];
    }

    public int BodyCount => _bodies.Length;

    /// <summary>VRM の揺れ物の、揺れるボーンの数。</summary>
    public int SpringJointCount => _springs?.JointCount ?? 0;

    /// <summary>
    /// 焼き込みで覚える置き方の数（剛体の数 ＋ 揺れるボーンの数）。<see cref="CopyTransforms"/> と <see cref="ApplyTransforms"/> は、
    /// 剛体の置き方の後ろに、揺れるボーンの置き方を並べる。
    /// </summary>
    public int TransformCount => _bodies.Length + SpringJointCount;

    /// <summary>剛体を押すインパルスモーフ（PMX 2.1）があるか。</summary>
    public bool HasImpulseMorphs => _impulseMorphs.Length > 0;

    public int JointCount { get; }

    /// <summary>物理で動く剛体の数（ボーン追従でないもの）。</summary>
    public int DynamicBodyCount => _bodies.Count(b => !b.Kinematic);

    /// <summary>次の計算で置き直しが要る（読み込んだ直後・物理を入れ直したとき）。</summary>
    public bool NeedsReset { get; set; } = true;

    /// <summary>
    /// PMX の剛体とジョイント（と VRM の揺れ物）から作る。どちらも無ければ null。使えないジョイントの種類などは <paramref name="report"/> で知らせる。
    /// </summary>
    public static ModelPhysics? Create(PmxDocument document, ModelRig rig, LoadReport? report = null)
    {
        SpringBoneSolver? springBones = SpringBoneSolver.Create(document.SpringBones, rig);
        if (document.RigidBodies.Length == 0 && springBones is null)
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
        return new ModelPhysics(world, rig, bodies, joints.Count, descs, springBones);
    }

    /// <summary>重力（モデルの座標で）。モデルを傾けて置いたときは、傾きに合わせて回したものを渡す。</summary>
    public void SetGravity(Vector3 gravity) => _world.SetGravity(gravity);

    /// <summary>床を置く・外す（モデルの座標で。物理で動く剛体が、床をすり抜けないようにする）。同じ床なら何もしない。</summary>
    public void SetFloor(PhysicsFloor floor)
    {
        if (floor == Floor)
        {
            return;
        }
        _world.SetFloor(floor);
        Floor = floor;
    }

    /// <summary>今の床。</summary>
    public PhysicsFloor Floor { get; private set; } = PhysicsFloor.None;

    /// <summary>ボーン追従の剛体（体など）の数。ほかのモデルの物理に、障害物として置くのに使う。</summary>
    public int FollowBodyCount => _followBodies.Length;

    /// <summary>ボーン追従の剛体の形・グループ（モデルの座標で。障害物として置くとき）。<paramref name="scale"/> で大きさを掛ける。</summary>
    public PhysicsBodyDesc[] FollowBodyDescs(float scale = 1f) =>
        [.. _followBodies.Select(i => _descs[i] with { Size = _descs[i].Size * scale, Kind = PhysicsBodyKind.Kinematic })];

    /// <summary>ボーン追従の剛体の、今のボーンの置き方での置き方（モデルの座標で）。<paramref name="state"/> は物理の前まで求めたもの。</summary>
    public void FollowBodyTransforms(PoseState state, Span<PhysicsTransform> destination)
    {
        for (int k = 0; k < _followBodies.Length && k < destination.Length; k++)
        {
            destination[k] = PhysicsTransform.FromMatrix(BodyFromBone(state, _followBodies[k]));
        }
    }

    /// <summary>障害物（ほかのモデルの体など。このモデルの座標で）の数。</summary>
    public int ObstacleCount => _obstacleTargets.Length;

    /// <summary>障害物を置き直す（前の障害物は外す）。置き方は <see cref="SetObstacleTargets"/> で与える。</summary>
    public void SetObstacles(ReadOnlySpan<PhysicsBodyDesc> obstacles)
    {
        _world.SetObstacles(obstacles);
        _obstacleTargets = new PhysicsTransform[obstacles.Length];
        for (int i = 0; i < obstacles.Length; i++)
        {
            _obstacleTargets[i] = obstacles[i].Transform;
        }
    }

    /// <summary>
    /// 障害物の目標の置き方（このモデルの座標で。数は <see cref="ObstacleCount"/>）。次の <see cref="Reset"/> ではすぐ置き、
    /// <see cref="Step"/> では前の置き方から少しずつ動かす。
    /// </summary>
    public void SetObstacleTargets(ReadOnlySpan<PhysicsTransform> targets)
    {
        if (targets.Length != _obstacleTargets.Length)
        {
            throw new ArgumentException($"障害物の目標の数（{targets.Length}）が障害物の数（{_obstacleTargets.Length}）と違います", nameof(targets));
        }
        targets.CopyTo(_obstacleTargets);
    }

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
        if (_obstacleTargets.Length > 0)
        {
            _world.SetObstacleTargets(_obstacleTargets, snap: true);
        }
        _world.Reset(_buffer);
        if (warmupSeconds > 0 && _bodies.Length > 0)
        {
            _world.Step(_buffer, warmupSeconds, Substeps(warmupSeconds));
        }
        NeedsReset = false;
        ApplyBodies(state);
        _springs?.Reset(state, warmupSeconds);
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
            if (_obstacleTargets.Length > 0)
            {
                _world.SetObstacleTargets(_obstacleTargets, snap: false);
            }
            if (_bodies.Length > 0)
            {
                _world.Step(_buffer, seconds, Substeps(seconds));
            }
        }
        ApplyBodies(state);
        _springs?.Step(state, seconds);
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

    /// <summary>今の剛体の置き方（と揺れ物の先端の位置）で、物理演算のボーンを置く（計算は進めない）。</summary>
    public void Apply(PoseState state)
    {
        if (NeedsReset)
        {
            Reset(state);
            return;
        }
        ApplyBodies(state);
        _springs?.Apply(state);
    }

    // 今の剛体の置き方で、剛体のボーンを置く
    private void ApplyBodies(PoseState state)
    {
        _world.GetTransforms(_buffer);
        ApplyBodyTransforms(state, _buffer);
        PoseEvaluator.RefreshPhysicsChildren(_rig, state);
    }

    /// <summary>
    /// 今の全剛体の置き方と、揺れるボーンの置き方を写す（焼き込みで覚えておくため）。<paramref name="destination"/> は <see cref="TransformCount"/> だけ要る。
    /// </summary>
    public void CopyTransforms(Span<PhysicsTransform> destination)
    {
        _world.GetTransforms(destination[.._bodies.Length]);
        _springs?.CopyTransforms(destination[_bodies.Length..]);
    }

    /// <summary>
    /// 与えた全剛体の置き方（焼き込みで覚えたものなど）で、物理演算のボーンを置く。
    /// <see cref="PoseEvaluator.EvaluateBeforePhysics"/> の後に呼ぶ（物理＋ボーン位置合わせは、そのときのボーンの位置を使う）。
    /// </summary>
    public void ApplyTransforms(PoseState state, ReadOnlySpan<PhysicsTransform> transforms)
    {
        if (transforms.Length != TransformCount)
        {
            throw new ArgumentException($"置き方の数（{transforms.Length}）が剛体と揺れるボーンの数（{TransformCount}）と違います", nameof(transforms));
        }
        ApplyBodyTransforms(state, transforms[.._bodies.Length]);
        _springs?.ApplyTransforms(state, transforms[_bodies.Length..]);
        PoseEvaluator.RefreshPhysicsChildren(_rig, state);
    }

    private void ApplyBodyTransforms(PoseState state, ReadOnlySpan<PhysicsTransform> transforms)
    {
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
