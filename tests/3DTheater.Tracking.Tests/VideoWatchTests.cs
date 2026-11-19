namespace Theater3D.Tracking.Tests;

/// <summary>カメラから映像が届いているかの見張りと、開き直しの決まり（設計判断記録 97 章）。</summary>
[TestClass]
public sealed class VideoWatchTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [TestMethod]
    public void SilenceIsReportedAfterAFewSeconds()
    {
        var watch = new VideoWatch();
        watch.Reset(S(0));
        watch.Opening(S(0));
        watch.Opened(S(0.5));
        Assert.IsNull(watch.Problem(S(2), "Surface Camera Front"), "開いてすぐは知らせない");
        string? problem = watch.Problem(S(4), "Surface Camera Front");
        Assert.IsNotNull(problem);
        Assert.Contains("「Surface Camera Front」から映像が届きません（3 秒）", problem, "開いてから数える");

        // 届けば消え、また届かなくなれば、最後に届いてから数える
        watch.FrameArrived(S(5));
        Assert.IsNull(watch.Problem(S(7), "Surface Camera Front"));
        Assert.Contains("（4 秒）", watch.Problem(S(9), "Surface Camera Front")!);

        // 開いている途中で止まっているときは「開きません」
        watch.Opening(S(20));
        Assert.Contains("カメラが開きません（4 秒）", watch.Problem(S(24), null)!);

        // 見張りをやめれば（止めた・スマホ）、何も出さない
        watch.Stop();
        Assert.IsNull(watch.Problem(S(100), "Surface Camera Front"));
    }

    [TestMethod]
    public void StalledVideoIsDetectedOnlyWhileReading()
    {
        var watch = new VideoWatch();
        watch.Reset(S(0));
        watch.Opening(S(0));
        Assert.IsFalse(watch.IsStalled(S(30)), "開いている途中は、やめさせられないので数えない");
        watch.Opened(S(1));
        Assert.IsFalse(watch.IsStalled(S(15.9)));
        Assert.IsTrue(watch.IsStalled(S(16)), "開いてから 15 秒届かない");
        watch.FrameArrived(S(16));
        Assert.IsFalse(watch.IsStalled(S(30)));
        Assert.IsTrue(watch.IsStalled(S(31)));

        // 開き直すのを待っている間は、止まったと数え直さない
        watch.Failed("15 秒届きません", S(31));
        Assert.IsFalse(watch.IsStalled(S(60)));
        watch.Stop();
        Assert.IsFalse(watch.IsStalled(S(1000)));
    }

    [TestMethod]
    public void FailuresAreRetriedWithGrowingWaitsUntilAFrameArrives()
    {
        var watch = new VideoWatch();
        watch.Reset(S(0));
        watch.Opened(S(0));

        // 1 回目・2 回目・3 回目は、2・5・10 秒あけて開き直す
        Assert.AreEqual(S(2), watch.Failed("映像の読み出しに失敗しました（0x800706BE）", S(24)));
        string waiting = watch.Problem(S(24.5), "Surface Camera Front")!;
        Assert.Contains("「Surface Camera Front」の映像を読めません（映像の読み出しに失敗しました（0x800706BE））", waiting);
        Assert.Contains("2 秒後に開き直します（1/3 回目）", waiting);
        watch.Opening(S(26));
        Assert.Contains("開き直しています（1/3 回目）", watch.Problem(S(26.5), "Surface Camera Front")!);
        Assert.AreEqual(S(5), watch.Failed("開けません", S(27)));
        Assert.AreEqual(S(10), watch.Failed("開けません", S(33)));
        Assert.AreEqual(3, watch.Failures);
        Assert.IsNull(watch.Failed("開けません", S(44)), "3 回開き直しても駄目なら、やめる");

        // 映像が 1 枚でも届けば、数え直す
        watch.Reset(S(100));
        watch.Failed("a", S(101));
        watch.Failed("b", S(104));
        watch.Opening(S(110));
        watch.FrameArrived(S(111));
        Assert.AreEqual(0, watch.Failures);
        Assert.IsNull(watch.Problem(S(112), "Surface Camera Front"), "届いたら、知らせを消す");
        Assert.AreEqual(S(2), watch.Failed("c", S(200)), "数え直した後は、また 2 秒から");
    }
}
