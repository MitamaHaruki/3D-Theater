namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class PlaybackServiceTests
{
    [TestMethod]
    public void DefaultsMatchSpecification()
    {
        var playback = new PlaybackService();
        Assert.AreEqual(30, playback.FrameRate, "30fps が標準（指示書 3.1）");
        Assert.AreEqual(0, playback.CurrentFrame);
        Assert.IsFalse(playback.IsPlaying);
    }

    [TestMethod]
    public void AdvanceMovesByElapsedTimeTimesFrameRate()
    {
        var playback = new PlaybackService();
        playback.Play();
        playback.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(30, playback.CurrentFrame);

        playback.FrameRate = 60;
        playback.Advance(TimeSpan.FromSeconds(0.5));
        Assert.AreEqual(60, playback.CurrentFrame);
    }

    [TestMethod]
    public void SmallStepsAccumulate()
    {
        var playback = new PlaybackService();
        playback.Play();
        for (int i = 0; i < 10; i++)
        {
            playback.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 75)); // 75Hz の画面更新（100ns 単位で切り捨て）
        }
        Assert.AreEqual(4, playback.CurrentFrame, "端数を捨てずに積み上げる（10/75 秒 × 30 = 4 フレーム）");
    }

    [TestMethod]
    public void DoesNotAdvanceWhilePaused()
    {
        var playback = new PlaybackService();
        playback.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, playback.CurrentFrame);
    }

    [TestMethod]
    public void LoopWrapsToStart()
    {
        var playback = new PlaybackService { EndFrame = 10 };
        playback.Seek(9);
        playback.Play();
        playback.Advance(TimeSpan.FromSeconds(3.0 / 30));
        Assert.AreEqual(1, playback.CurrentFrame, "9 → 10 → 0 → 1");
        Assert.IsTrue(playback.IsPlaying);
    }

    [TestMethod]
    public void WithoutLoopStopsAtEnd()
    {
        var playback = new PlaybackService { EndFrame = 10, Loop = false };
        playback.Seek(9);
        playback.Play();
        playback.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(10, playback.CurrentFrame);
        Assert.IsFalse(playback.IsPlaying);
    }

    [TestMethod]
    public void PlayAtEndRestartsFromBeginning()
    {
        var playback = new PlaybackService { EndFrame = 10 };
        playback.GoToEnd();
        playback.Play();
        Assert.AreEqual(0, playback.CurrentFrame);
    }

    [TestMethod]
    public void SeekClampsToRange()
    {
        var playback = new PlaybackService { EndFrame = 100 };
        playback.Seek(-5);
        Assert.AreEqual(0, playback.CurrentFrame);
        playback.Seek(500);
        Assert.AreEqual(100, playback.CurrentFrame);
        playback.Step(-1);
        Assert.AreEqual(99, playback.CurrentFrame);
    }

    [TestMethod]
    public void ShorteningEndMovesCurrentFrameInside()
    {
        var playback = new PlaybackService();
        playback.Seek(250);
        playback.EndFrame = 100;
        Assert.AreEqual(100, playback.CurrentFrame);
    }

    [TestMethod]
    public void ChangingFrameRateKeepsFrameNumber()
    {
        var playback = new PlaybackService();
        playback.Seek(120);
        playback.FrameRate = 24;
        Assert.AreEqual(120, playback.CurrentFrame, "キーはフレーム単位なので、番号は変えない");
    }

    [TestMethod]
    public void FrameChangedIsRaisedOnlyWhenFrameChanges()
    {
        var playback = new PlaybackService();
        int count = 0;
        playback.FrameChanged += (_, _) => count++;
        playback.Seek(5);
        playback.Seek(5);
        playback.Play();
        playback.Advance(TimeSpan.FromMilliseconds(10)); // 1 フレームに満たない
        Assert.AreEqual(1, count);
    }
}
