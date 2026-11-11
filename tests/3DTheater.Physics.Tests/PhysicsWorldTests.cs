using System.Numerics;

// 物理の世界はテストごとに別々に作るので、並べて動かしてよい
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Theater3D.Physics.Tests;

/// <summary>Bullet を包んだ物理の世界（C API と C# の受け渡し）を確かめる。</summary>
[TestClass]
public sealed class PhysicsWorldTests
{
    [TestMethod]
    public void TransformsAreInterpolatedForMotionBlur()
    {
        var a = new PhysicsTransform(new Vector3(0, 0, 0), Quaternion.Identity);
        var b = new PhysicsTransform(new Vector3(2, 4, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        PhysicsTransform half = PhysicsTransform.Interpolate(a, b, 0.5f);
        Assert.AreEqual(new Vector3(1, 2, 0), half.Position, "位置は直線");
        Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4);
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(expected, half.Rotation)), 1e-5f, "向きは球面で半分（45 度）");
        Assert.AreEqual(a.Position, PhysicsTransform.Interpolate(a, b, 0f).Position);
    }

    private const float Gravity = 98f;
    private static readonly PhysicsTransform Identity = new(Vector3.Zero, Quaternion.Identity);

    private static PhysicsBodyDesc Body(PhysicsShape shape, PhysicsBodyKind kind, Vector3 size, Vector3 position,
        uint group = 1, uint mask = 0xFFFF, float linearDamping = 0, float angularDamping = 0) => new()
    {
        Shape = shape,
        Kind = kind,
        Size = size,
        Mass = 1,
        Transform = new PhysicsTransform(position, Quaternion.Identity),
        LinearDamping = linearDamping,
        AngularDamping = angularDamping,
        Friction = 0.5f,
        Group = group,
        Mask = mask,
    };

    private static PhysicsTransform[] Read(PhysicsWorld world)
    {
        var result = new PhysicsTransform[world.BodyCount];
        world.GetTransforms(result);
        return result;
    }

    [TestMethod]
    public void ApiVersionMatches()
    {
        var (major, minor) = PhysicsWorld.ApiVersion;
        Assert.AreEqual(PhysicsWorld.ExpectedApiVersionMajor, major);
        Assert.IsGreaterThanOrEqualTo(PhysicsWorld.ExpectedApiVersionMinor, minor);
    }

    [TestMethod]
    public void DynamicBodyFallsUnderGravity()
    {
        using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
        world.AddBodies([Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, new Vector3(0.5f), new Vector3(0, 10, 0))]);

        world.Step([Identity], 0.5f, 60);

        float y = Read(world)[0].Position.Y;
        Assert.AreEqual(10 - (0.5f * Gravity * 0.25f), y, 0.5f, "自由落下（y = 10 - g t² / 2）");
    }

    [TestMethod]
    public void JointMakesAPendulumHangBelowTheAnchor()
    {
        using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
        world.AddBodies(
        [
            Body(PhysicsShape.Box, PhysicsBodyKind.Kinematic, new Vector3(0.2f), new Vector3(0, 10, 0), group: 1, mask: 0),
            Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, new Vector3(0.3f), new Vector3(5, 10, 0), group: 2, mask: 0,
                linearDamping: 0.9f, angularDamping: 0.9f),
        ]);
        world.AddJoints(
        [
            new PhysicsJointDesc
            {
                BodyA = 0,
                BodyB = 1,
                Transform = new PhysicsTransform(new Vector3(0, 10, 0), Quaternion.Identity),
                // 位置は固定、回転は自由（下限 > 上限）
                AngularLower = new Vector3(1),
                AngularUpper = new Vector3(-1),
            },
        ]);
        PhysicsTransform anchor = new(new Vector3(0, 10, 0), Quaternion.Identity);

        world.Step([anchor, Identity], 3f, 360);

        Vector3 bob = Read(world)[1].Position;
        Assert.AreEqual(5f, Vector3.Distance(bob, anchor.Position), 0.2f, "ジョイントからの距離は変わらない");
        Assert.IsLessThan(10f - 4.5f, bob.Y, "揺れが収まって、つないだ所の真下に近い");
    }

    [TestMethod]
    public void CollisionMaskDecidesWhoCollides()
    {
        float Drop(uint mask)
        {
            using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
            world.AddBodies(
            [
                Body(PhysicsShape.Box, PhysicsBodyKind.Kinematic, new Vector3(10, 1, 10), Vector3.Zero, group: 1 << 0),
                Body(PhysicsShape.Box, PhysicsBodyKind.Dynamic, new Vector3(0.5f), new Vector3(0, 5, 0), group: 1 << 1, mask: mask),
            ]);
            PhysicsTransform floor = new(Vector3.Zero, Quaternion.Identity);
            world.Step([floor, Identity], 2f, 240);
            return Read(world)[1].Position.Y;
        }

        Assert.AreEqual(1.5f, Drop(0xFFFF), 0.1f, "床（グループ 0）とぶつかる相手なら、床の上に止まる");
        Assert.IsLessThan(-5f, Drop(0xFFFE), "床のグループのビットが 0 なら、すり抜ける");
    }

    [TestMethod]
    public void DynamicBodiesRestOnTheFloorWhateverTheirMask()
    {
        float Drop(PhysicsFloor floor, uint mask = 0xFFFF, PhysicsBodyKind kind = PhysicsBodyKind.Dynamic, float startY = 5f)
        {
            using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
            world.AddBodies([Body(PhysicsShape.Sphere, kind, new Vector3(1f), new Vector3(0, startY, 0), group: 1 << 3, mask: mask)]);
            world.SetFloor(floor);
            var target = new PhysicsTransform(new Vector3(0, startY, 0), Quaternion.Identity);
            world.Step([target], 2f, 240);
            return Read(world)[0].Position.Y;
        }

        var level = new PhysicsFloor(true, Vector3.Zero, Vector3.UnitY);
        Assert.AreEqual(1f, Drop(level), 0.1f, "動く剛体は床（高さ 0）の上に止まる（半径 1 の球）");
        Assert.AreEqual(1f, Drop(level, mask: 0), 0.1f, "ぶつかる相手のビットに関わらず、床には当たる");
        Assert.IsLessThan(-5f, Drop(PhysicsFloor.None), "床が無ければ落ちていく");
        Assert.AreEqual(3f, Drop(new PhysicsFloor(true, new Vector3(5, 2, 7), new Vector3(0, 10, 0))), 0.1f,
            "床は point を通る（向きの長さは問わない）");
        Assert.AreEqual(-3f, Drop(level, kind: PhysicsBodyKind.Kinematic, startY: -3f), 1e-4f, "外から置き方を与える剛体は、床に押し返されない");

        using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
        world.AddBodies([Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, new Vector3(1f), new Vector3(0, 5, 0))]);
        world.SetFloor(level);
        world.SetFloor(PhysicsFloor.None);
        world.Step([Identity], 2f, 240);
        Assert.IsLessThan(-5f, Read(world)[0].Position.Y, "床を外すと、すり抜ける");
        Assert.AreEqual(1, world.BodyCount, "床は剛体の数に入らない");
        Assert.ThrowsExactly<PhysicsException>(() => world.SetFloor(new PhysicsFloor(true, Vector3.Zero, Vector3.Zero)), "向きが 0 の床は誤り");
        Assert.ThrowsExactly<PhysicsException>(() => world.SetFloor(new PhysicsFloor(true, new Vector3(float.NaN), Vector3.UnitY)));
    }

    [TestMethod]
    public void ObstaclesStopDynamicBodiesWithoutBeingCounted()
    {
        float Drop(uint obstacleMask, float obstacleY = 0f)
        {
            using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
            world.AddBodies([Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, new Vector3(1f), new Vector3(0, 5, 0), group: 1 << 1)]);
            world.SetObstacles([Body(PhysicsShape.Box, PhysicsBodyKind.Kinematic, new Vector3(4, 1, 4), Vector3.Zero, group: 1 << 2, mask: obstacleMask)]);
            world.SetObstacleTargets([new PhysicsTransform(new Vector3(0, obstacleY, 0), Quaternion.Identity)], snap: true);
            world.Step([Identity], 2f, 240);
            Assert.AreEqual(1, world.BodyCount, "障害物は剛体の数に入らない");
            return Read(world)[0].Position.Y;
        }

        Assert.AreEqual(2f, Drop(0xFFFF), 0.1f, "障害物（厚さの半分 1）の上に止まる（半径 1 の球）");
        Assert.IsLessThan(-5f, Drop(0), "障害物のぶつかる相手に入っていなければ、すり抜ける");
        Assert.AreEqual(-48f, Drop(0xFFFF, obstacleY: -50f), 0.2f, "障害物は与えた置き方の所で当たる");

        using var empty = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
        empty.SetObstacles([Body(PhysicsShape.Sphere, PhysicsBodyKind.Kinematic, new Vector3(1f), Vector3.Zero)]);
        Assert.ThrowsExactly<PhysicsException>(() => empty.SetObstacleTargets([], snap: false), "目標の数は障害物の数と同じ");
        empty.SetObstacles([]);
        empty.SetObstacleTargets([], snap: false);
    }

    [TestMethod]
    public void TransformsMoveBetweenModelCoordinates()
    {
        // ほかのモデル: (5, 0, 0) に Y 軸まわりに 90° 回して 2 倍で置いた。このモデル: 原点にそのまま
        var other = new ModelPlacement(new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), 2f);
        var self = new ModelPlacement(Vector3.Zero, Quaternion.Identity, 1f);
        PhysicsTransform moved = self.FromModel(new PhysicsTransform(new Vector3(1, 0, 0), Quaternion.Identity), other);

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(5, 0, -2), moved.Position), 1e-5f, "拡大 → 回転 → 移動の順に置く");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 0, -1), Vector3.Transform(Vector3.UnitX, moved.Rotation)), 1e-5f, "向きも回る");
        Assert.AreEqual(2f, self.ScaleFrom(other), "ほかのモデルの長さ 1 は、このモデルの座標で 2");
        PhysicsTransform back = other.FromModel(moved, self);
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(1, 0, 0), back.Position), 1e-5f, "戻すと元の置き方");
    }

    [TestMethod]
    public void SceneFloorIsExpressedInModelCoordinates()
    {
        // モデルを高さ 3 に置けば、モデルの座標では床は高さ -3。2 倍に拡大していれば -1.5
        PhysicsFloor raised = PhysicsFloor.SceneFloorInModel(Quaternion.Identity, new Vector3(4, 3, 5), 2f);
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, -1.5f, 0), raised.Point), 1e-5f);
        Assert.AreEqual(Vector3.UnitY, raised.Normal);
        // 前へ 90° 倒したモデル（X 軸まわり）: モデルの座標では、床の向きはモデルの -Z か +Z を向く
        Quaternion tilted = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        PhysicsFloor floor = PhysicsFloor.SceneFloorInModel(tilted, new Vector3(0, 2, 0), 1f);
        Vector3 worldNormal = Vector3.Transform(floor.Normal, tilted);
        Assert.AreEqual(0f, Vector3.Distance(Vector3.UnitY, worldNormal), 1e-5f, "シーンに戻すと上向き");
        Vector3 worldPoint = Vector3.Transform(floor.Point, tilted) + new Vector3(0, 2, 0);
        Assert.AreEqual(0f, worldPoint.Y, 1e-5f, "シーンに戻すと高さ 0");
    }

    [TestMethod]
    public void ResetPlacesBodiesAndStopsThem()
    {
        using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
        world.AddBodies([Body(PhysicsShape.Capsule, PhysicsBodyKind.Dynamic, new Vector3(0.5f, 1, 0), new Vector3(0, 10, 0))]);
        world.Step([Identity], 0.5f, 60); // 落ちて速さが付く

        var start = new PhysicsTransform(new Vector3(3, 20, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f));
        world.Reset([start]);
        world.Step([Identity], 1f / 120f, 1);

        PhysicsTransform after = Read(world)[0];
        Assert.AreEqual(20f, after.Position.Y, 0.05f, "置き直すと速さが 0 に戻る（1/120 秒ではほとんど落ちない）");
        Assert.AreEqual(3f, after.Position.X, 1e-3f);
    }

    [TestMethod]
    public void WrongCountsAndValuesAreRejected()
    {
        using var world = PhysicsWorld.Create(new Vector3(0, -Gravity, 0));
        world.AddBodies([Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, Vector3.One, Vector3.Zero)]);

        var ex = Assert.ThrowsExactly<PhysicsException>(() => world.Step([Identity, Identity], 0.1f, 1));
        StringAssert.Contains(ex.Message, "剛体の数");
        Assert.ThrowsExactly<PhysicsException>(() => world.AddJoints([new PhysicsJointDesc { BodyA = 0, BodyB = 0 }]));
        Assert.ThrowsExactly<PhysicsException>(() => world.Reset([new PhysicsTransform(new Vector3(float.NaN), Quaternion.Identity)]));

        world.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => world.Step([Identity], 0.1f, 1));
    }

    [TestMethod]
    public void ImpulsesChangeTheVelocityAndStopClearsIt()
    {
        using var world = PhysicsWorld.Create(Vector3.Zero);
        PhysicsBodyDesc turned = Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, new Vector3(0.5f), new Vector3(10, 0, 0), mask: 0);
        turned.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2); // 剛体の +X が世界の -Z を向く
        world.AddBodies(
        [
            Body(PhysicsShape.Sphere, PhysicsBodyKind.Dynamic, new Vector3(0.5f), Vector3.Zero, mask: 0),
            turned,
            Body(PhysicsShape.Sphere, PhysicsBodyKind.Kinematic, new Vector3(0.5f), new Vector3(0, 5, 0), mask: 0),
        ]);
        PhysicsTransform[] targets = [Identity, Identity, new PhysicsTransform(new Vector3(0, 5, 0), Quaternion.Identity)];

        world.ApplyImpulses(
        [
            new PhysicsImpulse { Body = 0, Linear = new Vector3(2, 0, 0) },
            new PhysicsImpulse { Body = 1, Flags = PhysicsImpulseFlags.Local, Linear = new Vector3(2, 0, 0) },
            new PhysicsImpulse { Body = 2, Linear = new Vector3(5, 0, 0) },
        ]);
        world.Step(targets, 1f, 120);
        PhysicsTransform[] after = Read(world);
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(2, 0, 0), after[0].Position), 0.05f, "重さ 1 なら、勢い 2 で 1 秒に 2 進む");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(10, 0, -2), after[1].Position), 0.05f, "剛体の向きで回してから押す");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 5, 0), after[2].Position), 1e-3f, "外から置き方を与える剛体は押さない");

        world.ApplyImpulses(
        [
            new PhysicsImpulse { Body = 0, Flags = PhysicsImpulseFlags.Stop },
            new PhysicsImpulse { Body = 1, Flags = PhysicsImpulseFlags.Stop, Linear = new Vector3(0, 1, 0) }, // 止めてから押す
        ]);
        world.Step(targets, 1f, 120);
        after = Read(world);
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(2, 0, 0), after[0].Position), 0.05f, "止めると、その場に止まる");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(10, 1, -2), after[1].Position), 0.05f, "止めてから押すと、前の速さは残らない");

        Assert.ThrowsExactly<PhysicsException>(() => world.ApplyImpulses([new PhysicsImpulse { Body = 3 }]));
        Assert.ThrowsExactly<PhysicsException>(() => world.ApplyImpulses([new PhysicsImpulse { Body = 0, Linear = new Vector3(float.NaN) }]));
    }
}
