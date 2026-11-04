// エンジンはスレッドに結びつくうえ、ログの受け口がプロセスで 1 つなので、テストは順番に動かす
[assembly: DoNotParallelize]

namespace Theater3D.Engine.Interop.Tests;

/// <summary>C# からネイティブエンジンを呼べること、約束ごと（破棄の順番など）が守られることを確かめる。</summary>
[TestClass]
public sealed class NativeEngineTests
{
    // 背景色 (0.13, 0.14, 0.16) を BGRA 8 ビットにしたもの
    private static readonly byte[] BackgroundBgra = [41, 36, 33, 255];

    [TestMethod]
    public void ApiVersionMatchesExpected()
    {
        var (major, minor) = NativeEngine.ApiVersion;
        Assert.AreEqual(NativeEngine.ExpectedApiVersionMajor, major);
        Assert.IsGreaterThanOrEqualTo(NativeEngine.ExpectedApiVersionMinor, minor);
    }

    [TestMethod]
    public void WarpEngineReportsInfo()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        Assert.IsTrue(engine.Info.IsSoftware);
        Assert.IsFalse(string.IsNullOrWhiteSpace(engine.Info.AdapterName));
        StringAssert.Matches(engine.Info.FeatureLevel, new System.Text.RegularExpressions.Regex(@"^11_[01]$"));
    }

    [TestMethod]
    public void OffscreenRenderProducesBackgroundAndCube()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 128;
        const int height = 96;
        using (var view = engine.CreateOffscreenView(width, height))
        {
            Assert.IsTrue(view.Render(0f));
            var pixels = new byte[width * height * 4];
            view.ReadPixels(pixels, width * 4);

            CollectionAssert.AreEqual(BackgroundBgra, pixels[..4], "左上は背景色であること");
            int center = ((height / 2 * width) + (width / 2)) * 4;
            CollectionAssert.AreNotEqual(BackgroundBgra, pixels[center..(center + 4)], "中央には立方体が写ること");
        }
    }

    [TestMethod]
    public void DisposingEngineBeforeViewsIsRejected()
    {
        var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        var view = engine.CreateOffscreenView(8, 8);

        var ex = Assert.ThrowsExactly<NativeEngineException>(engine.Dispose);
        Assert.AreEqual(NativeResult.InvalidState, ex.Result);
        Assert.IsFalse(engine.IsDisposed);

        view.Dispose();
        engine.Dispose();
        Assert.IsTrue(engine.IsDisposed);
    }

    [TestMethod]
    public void UsingDisposedObjectsThrows()
    {
        var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        var view = engine.CreateOffscreenView(8, 8);
        view.Dispose();
        view.Dispose(); // 2 回目は何もしない
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.Render(0f));

        engine.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.CreateOffscreenView(8, 8));
    }

    [TestMethod]
    public void CallsFromOtherThreadAreRejected()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        NativeEngineException? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                engine.QueryVideoMemory();
            }
            catch (NativeEngineException ex)
            {
                caught = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.IsNotNull(caught);
        Assert.AreEqual(NativeResult.WrongThread, caught.Result);
        StringAssert.Contains(caught.Message, "スレッド", "失敗の説明が C# 側まで届くこと");
    }

    [TestMethod]
    public void SharedViewIsNotSupportedOnWarp()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        var ex = Assert.ThrowsExactly<NativeEngineException>(() => engine.CreateOffscreenView(16, 16, shareWithD3D9: true));
        Assert.AreEqual(NativeResult.NotSupported, ex.Result);
    }

    [TestMethod]
    public void NativeLogMessagesReachManagedCode()
    {
        var messages = new List<string>();
        void Handler(NativeLogLevel level, string message)
        {
            lock (messages)
            {
                messages.Add(message);
            }
        }

        NativeLog.MessageReceived += Handler;
        try
        {
            using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        }
        finally
        {
            NativeLog.MessageReceived -= Handler;
        }

        lock (messages)
        {
            Assert.IsTrue(messages.Exists(m => m.Contains("Direct3D 11")), "初期化のログが届くこと");
        }
    }
}
