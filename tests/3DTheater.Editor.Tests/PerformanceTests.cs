using System.Runtime.InteropServices;

namespace Theater3D.Editor.Tests;

/// <summary>ライブモードの速さのログ: 時間の分かれ方・トラッキングと描画の数え方・メモリの増え方・CPU の割合・電源・ログの文。</summary>
[TestClass]
public sealed class PerformanceTests
{
    [TestMethod]
    public void HistogramGivesMeanPercentileAndMax()
    {
        var histogram = new TimeHistogram();
        for (int i = 1; i <= 100; i++)
        {
            histogram.Add(i);
        }
        TimeSummary summary = histogram.Summarize();
        Assert.AreEqual(100, summary.Count);
        Assert.AreEqual(50.5, summary.Mean, 1e-9);
        Assert.AreEqual(100, summary.Max);
        Assert.AreEqual(95, summary.P95, TimeHistogram.BucketMilliseconds, "95% の値は箱の幅の分だけずれてよい");
        Assert.AreEqual(1, histogram.Percentile(0), TimeHistogram.BucketMilliseconds);

        histogram.Add(1000); // 250 ms より長いものは最後の箱
        Assert.AreEqual(1000, histogram.Percentile(1), "最後の箱は最大の値");
        histogram.Add(double.NaN);
        histogram.Add(-5);
        Assert.AreEqual(102, histogram.Count, "NaN は数えない。負の値は 0 にする");

        var other = new TimeHistogram();
        other.Add(2000);
        histogram.Merge(other);
        Assert.AreEqual(103, histogram.Count);
        Assert.AreEqual(2000, histogram.Max);

        histogram.Clear();
        Assert.AreEqual(new TimeSummary(0, 0, 0, 0), histogram.Summarize());
    }

    [TestMethod]
    public void TrackingStatsCountOnlyWhileRunning()
    {
        var time = new ManualTime();
        var stats = new TrackingStats(time);
        Assert.IsNull(stats.Finish(), "始めていなければ、まとめは無い");

        stats.Begin(phone: false);
        time.Advance(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 300; i++)
        {
            stats.AddCameraFrame(10, 20, found: i < 270, detected: i % 100 == 0);
        }
        TrackingPeriod first = stats.TakeInterval();
        Assert.AreEqual(TimeSpan.FromSeconds(10), first.Duration);
        Assert.AreEqual(300, first.Frames);
        Assert.AreEqual(30, first.FramesPerSecond, 1e-9);
        Assert.AreEqual(0.9, first.FoundRatio, 1e-9);
        Assert.AreEqual(3, first.Detected);
        Assert.AreEqual(10, first.Process.Mean, 1e-9);
        Assert.AreEqual(20, first.Wait.Mean, 1e-9);
        Assert.IsFalse(first.Phone);

        time.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 150; i++)
        {
            stats.AddCameraFrame(12, 20, found: true, detected: false);
        }
        TrackingPeriod? run = stats.Finish();
        Assert.IsNotNull(run);
        Assert.AreEqual(TimeSpan.FromSeconds(15), run.Duration, "1 回分は、始めてから止めるまで");
        Assert.AreEqual(450, run.Frames);
        Assert.IsFalse(stats.IsRunning);

        time.Advance(TimeSpan.FromSeconds(30)); // 止めている間は数えない
        TrackingPeriod second = stats.TakeInterval();
        Assert.AreEqual(TimeSpan.FromSeconds(5), second.Duration, "区切りは、前に読んでから止めるまで");
        Assert.AreEqual(150, second.Frames);
        Assert.IsTrue(stats.TakeInterval().IsEmpty, "止めた後の区切りは空");

        stats.Begin(phone: true);
        time.Advance(TimeSpan.FromSeconds(2));
        stats.AddPhoneFrame(found: true);
        stats.AddPhoneFrame(found: false);
        TrackingPeriod total = stats.Total;
        Assert.AreEqual(TimeSpan.FromSeconds(17), total.Duration);
        Assert.AreEqual(452, total.Frames);
        Assert.IsTrue(total.Phone);
        Assert.AreEqual(450, total.Process.Count, "スマホの分は、処理の時間に入れない");
        Assert.AreEqual(0.5, stats.Finish()!.FoundRatio, 1e-9);
    }

    [TestMethod]
    public void RenderStatsCountFramesHitchesAndPauses()
    {
        var time = new ManualTime();
        var stats = new RenderStats(time);
        stats.AddTick(16.7);
        stats.AddTick(16.7);
        stats.AddTick(60); // つかえ
        stats.AddTick(1500); // 止まった（窓を小さくしたときなど）
        stats.AddFrame(2, 3);
        stats.AddFrame(4, null);
        time.Advance(TimeSpan.FromSeconds(2));

        RenderPeriod period = stats.TakeInterval();
        Assert.AreEqual(TimeSpan.FromSeconds(2), period.Duration);
        Assert.AreEqual(4, period.Ticks);
        Assert.AreEqual(2, period.Frames);
        Assert.AreEqual(1, period.FramesPerSecond, 1e-9);
        Assert.AreEqual(1, period.Hitches);
        Assert.AreEqual(1, period.Pauses);
        Assert.AreEqual(60, period.LongestStep, "止まった間は、いちばん長い間に入れない");
        Assert.AreEqual(3, period.Frame.Mean, 1e-9);
        Assert.AreEqual(1, period.Gpu.Count, "GPU の時間は、測れた分だけ");

        time.Advance(TimeSpan.FromSeconds(1));
        stats.AddFrame(1, 1);
        RenderPeriod next = stats.TakeInterval();
        Assert.AreEqual(1, next.Frames, "区切りは数え直す");
        Assert.AreEqual(0, next.Hitches);
        Assert.AreEqual(3, stats.Total.Frames, "全体は数え直さない");
        Assert.AreEqual(TimeSpan.FromSeconds(3), stats.Total.Duration);
    }

    [TestMethod]
    public void MemoryTrendIgnoresWarmUp()
    {
        var trend = new MemoryTrend();
        trend.Add(TimeSpan.FromMinutes(0), 300);
        trend.Add(TimeSpan.FromMinutes(1), 450); // 起動した直後は、モデルの読み込みなどで増える
        Assert.IsNull(trend.GrowthPerHour);
        trend.Add(TimeSpan.FromMinutes(2), 600);
        trend.Add(TimeSpan.FromMinutes(3), 600.2);
        Assert.IsNull(trend.GrowthPerHour, $"{MemoryTrend.MinimumSamples} 個より少なければ求めない");
        for (int minute = 4; minute <= 10; minute++)
        {
            trend.Add(TimeSpan.FromMinutes(minute), 600 + (0.2 * (minute - 2))); // 1 時間で 12 MB 増える
        }
        Assert.AreEqual(12, trend.GrowthPerHour!.Value, 1e-6);
        Assert.AreEqual(TimeSpan.FromMinutes(2), trend.GrowthFrom);
        Assert.AreEqual(300, trend.First);
        Assert.AreEqual(601.6, trend.Peak!.Value, 1e-9);
        Assert.AreEqual(601.6, trend.Last!.Value, 1e-9);

        var flat = new MemoryTrend();
        for (int minute = 0; minute <= 6; minute++)
        {
            flat.Add(TimeSpan.FromMinutes(minute), 849);
        }
        Assert.AreEqual(0, flat.GrowthPerHour!.Value, 1e-9);
    }

    [TestMethod]
    public void CpuPercentIsShareOfWholeMachine()
    {
        Assert.AreEqual(25, PerformanceMath.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4), 1e-9);
        Assert.AreEqual(100, PerformanceMath.CpuPercent(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1), 4), "100% を超えない");
        Assert.AreEqual(0, PerformanceMath.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.AreEqual(0, PerformanceMath.CpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 4));
    }

    [TestMethod]
    public void PowerIsDescribed()
    {
        Assert.AreEqual(
            "電池 78%・省エネ機能 オン・電源モード「最適な電力効率」",
            new PowerState(true, true, 78, true, PowerState.BestEfficiency).Describe());
        Assert.AreEqual("コンセント・電源モード「バランス」", new PowerState(false, false, null, false, PowerState.Balanced).Describe());
        Assert.AreEqual(
            "コンセント（電池 100%）・電源モード「最適なパフォーマンス」",
            new PowerState(true, false, 100, false, PowerState.BestPerformance).Describe());
        Assert.AreEqual("電源 不明", new PowerState(false, null, null, false, null).Describe());
        StringAssert.StartsWith(PowerState.ModeName(Guid.Parse("3af9b8d9-7c97-431d-ad78-34a8bfea439f")), "そのほか");
        Assert.IsNull(PowerState.ModeName(null));
    }

    [TestMethod]
    public void MachineTellsEmulation()
    {
        Assert.AreEqual("ARM64（x64 の版を、まねて動かしている）", PerformanceText.Machine(Architecture.X64, Architecture.Arm64));
        Assert.AreEqual("x64", PerformanceText.Machine(Architecture.X64, Architecture.X64));
        Assert.AreEqual("x64", PerformanceText.Machine(Architecture.X64, null));
    }

    [TestMethod]
    public void DurationsAreWrittenInJapanese()
    {
        Assert.AreEqual("45 秒", PerformanceText.Duration(TimeSpan.FromSeconds(45)));
        Assert.AreEqual("1 分", PerformanceText.Duration(TimeSpan.FromSeconds(60.2)));
        Assert.AreEqual("1 分 30 秒", PerformanceText.Duration(TimeSpan.FromSeconds(90)));
        Assert.AreEqual("1 時間 5 分", PerformanceText.Duration(TimeSpan.FromMinutes(65)));
        Assert.AreEqual("2 時間", PerformanceText.Duration(TimeSpan.FromHours(2)));
    }

    [TestMethod]
    public void LogLinesHaveTheNumbers()
    {
        var render = new RenderPeriod(TimeSpan.FromMinutes(1), 3600, 1800, 2, 0, 75, new TimeSummary(1800, 2.14, 3.0, 9), new TimeSummary(1800, 3.4, 4, 6));
        var tracking = new TrackingPeriod(false, TimeSpan.FromSeconds(30), 900, 882, 3, new TimeSummary(900, 12.3, 18.1, 40.2), new TimeSummary(900, 20.1, 30, 33));
        string speed = PerformanceText.Speed(TimeSpan.FromMinutes(1), render, tracking, new VirtualCameraPeriod(30, InUse: true));
        Assert.AreEqual(
            "[速さ] 1 分: 描画 30.0 回/秒（1 回 平均 2.1 ms・95% 3.0 ms・GPU 3.4 ms・つかえ 2 回（いちばん長い間 75 ms））"
            + "／トラッキング 30.0 回/秒（動かしていた 30 秒・処理 平均 12.3 ms・95% 18.1 ms・最大 40.2 ms・映像待ち 平均 20.1 ms・顔あり 98%・探し直し 3 回）"
            + "／仮想カメラ 30.0 回/秒（今、ほかのアプリが使っている）",
            speed);
        StringAssert.Contains(PerformanceText.Speed(TimeSpan.FromMinutes(1), render, null, null), "トラッキング 止めている");
        var phone = new TrackingPeriod(true, TimeSpan.FromMinutes(1), 3600, 3600, 0, default, default);
        Assert.AreEqual("トラッキング（スマホ） 60.0 回/秒（1 分・顔あり 100%）", PerformanceText.Tracking(phone));

        var load = new LoadSample(23.4, 1120.4, 849.6, 120.2, 1234, 45, 0.423, new PowerState(true, true, 78, false, PowerState.Balanced));
        Assert.AreEqual(
            "[重さ] CPU 23%（PC 全体のうち）・メモリ 1120 MB（実際に載っている分 850 MB・.NET 120 MB・ハンドル 1234・スレッド 45）"
            + "・GPU のメモリ 0.42 GB・電源 電池 78%・電源モード「バランス」",
            PerformanceText.Load(load));

        var memory = new MemoryTrend();
        for (int minute = 0; minute <= 10; minute++)
        {
            memory.Add(TimeSpan.FromMinutes(minute), minute < 2 ? 500 : 600 + (0.2 * (minute - 2)));
        }
        var summary = new SessionSummary(
            TimeSpan.FromMinutes(10.5), render, tracking, 22.4, 35.1, memory, 0.45,
            new PowerState(true, true, 80, false, PowerState.Balanced), new PowerState(true, true, 72, false, PowerState.Balanced));
        string text = PerformanceText.Summary(summary);
        StringAssert.StartsWith(text, "[まとめ] ライブモード 10 分 30 秒: 描画 平均 30.0 回/秒（つかえ 2 回）");
        StringAssert.Contains(text, "トラッキング 計 30 秒（30.0 回/秒・処理 平均 12.3 ms");
        StringAssert.Contains(text, "CPU 平均 22%・最大 35%");
        StringAssert.Contains(text, "メモリ 始め 500 MB・最大 602 MB・終わり 602 MB・増え方 +12 MB/時（2 分目から）");
        StringAssert.Contains(text, "GPU のメモリ 最大 0.45 GB");
        StringAssert.Contains(text, "電源 電池 80%・電源モード「バランス」 → 電池 72%・電源モード「バランス」");

        var shortRun = new SessionSummary(TimeSpan.FromSeconds(50), render, null, 10, 10, new MemoryTrend(), null, null, null);
        Assert.AreEqual("[まとめ] ライブモード 50 秒: 描画 平均 30.0 回/秒（つかえ 2 回）／CPU 平均 10%・最大 10%", PerformanceText.Summary(shortRun));
    }

    /// <summary>手で進める時計（1 刻み = 100 ナノ秒）。</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
