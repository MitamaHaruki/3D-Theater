namespace Theater3D.Editor.Tests;

/// <summary>物理演算を進める・置き直すの決め方。</summary>
[TestClass]
public sealed class PhysicsClockTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 60);

    [TestMethod]
    public void PlaybackStepsByTheFramesThatAdvanced()
    {
        var clock = new PhysicsClock();
        Assert.AreEqual(PhysicsStepKind.Reset, clock.Next(0, playing: true, 30, Tick, T0).Kind, "最初は置き直す");

        Assert.AreEqual(PhysicsStep.None, clock.Next(0, true, 30, Tick, T0), "フレームが変わらなければ進めない");
        PhysicsStep one = clock.Next(1, true, 30, Tick, T0);
        Assert.AreEqual(PhysicsStepKind.Step, one.Kind);
        Assert.AreEqual(1f / 30f, one.Seconds, 1e-6f);
        Assert.AreEqual(3f / 30f, clock.Next(4, true, 30, Tick, T0).Seconds, 1e-6f, "画面の更新が遅れて 3 フレーム進んだら、3 フレーム分");
        Assert.AreEqual(PhysicsStepKind.Reset, clock.Next(0, true, 30, Tick, T0).Kind, "ループで先頭へ戻ったら置き直す");
        Assert.AreEqual(PhysicsStepKind.Reset, clock.Next(20, true, 30, Tick, T0).Kind, "大きく飛んだら置き直す");
    }

    [TestMethod]
    public void WhenPausedJumpsResetAndSingleFramesStep()
    {
        var clock = new PhysicsClock();
        clock.Next(10, false, 60, Tick, T0);

        Assert.AreEqual(PhysicsStep.None, clock.Next(10, false, 60, Tick, T0), "何もしなければ止まったまま");
        Assert.AreEqual(new PhysicsStep(PhysicsStepKind.Step, 1f / 60f), clock.Next(11, false, 60, Tick, T0), "1 フレーム送りは続けて計算する");
        Assert.AreEqual(PhysicsStepKind.Reset, clock.Next(9, false, 60, Tick, T0).Kind, "戻したら置き直す");
        Assert.AreEqual(PhysicsStepKind.Reset, clock.Next(50, false, 60, Tick, T0).Kind, "飛ばしたら置き直す");
    }

    [TestMethod]
    public void EditingWhilePausedSettlesForAWhile()
    {
        var clock = new PhysicsClock();
        clock.Next(0, false, 30, Tick, T0);
        clock.NoteEdit(T0);

        PhysicsStep settle = clock.Next(0, false, 30, Tick, T0.AddSeconds(0.5));
        Assert.AreEqual(PhysicsStepKind.Step, settle.Kind, "姿勢を変えた後は、実際の時間で動かす");
        Assert.AreEqual(1f / 60f, settle.Seconds, 1e-6f);
        Assert.AreEqual(PhysicsClock.MaxSettleStep, clock.Next(0, false, 30, TimeSpan.FromSeconds(1), T0.AddSeconds(1)).Seconds, "遅れても大きく飛ばない");
        Assert.IsTrue(clock.IsSettling(T0.AddSeconds(1.9)));
        Assert.AreEqual(PhysicsStep.None, clock.Next(0, false, 30, Tick, T0.AddSeconds(2.1)), "しばらくしたら止める");
    }

    [TestMethod]
    public void RequestedResetComesFirst()
    {
        var clock = new PhysicsClock();
        clock.Next(0, true, 30, Tick, T0);
        clock.RequestReset();

        Assert.AreEqual(PhysicsStepKind.Reset, clock.Next(1, true, 30, Tick, T0).Kind);
        Assert.AreEqual(PhysicsStepKind.Step, clock.Next(2, true, 30, Tick, T0).Kind);
    }
}
