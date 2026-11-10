// エンジンはスレッドに結びつくうえ、ログの受け口がプロセスで 1 つなので、テストは順番に動かす
[assembly: DoNotParallelize]

namespace Theater3D.Engine.Interop.Tests;

/// <summary>C# からネイティブエンジンを呼べること、約束ごと（破棄の順番など）が守られることを確かめる。</summary>
[TestClass]
public sealed class NativeEngineTests
{
    // 背景色 (0.13, 0.14, 0.16) を BGRA 8 ビットにしたもの
    private static readonly byte[] BackgroundBgra = [41, 36, 33, 255];

    // 斜め上から確認用の立方体（中心 (0, 0.5, 0)）を見る描画条件
    private static readonly FrameParameters DefaultFrame = new(
        FrameFlags.ShowGrid | FrameFlags.ShowAxisX | FrameFlags.ShowAxisZ | FrameFlags.ShowTestObject,
        System.Numerics.Matrix4x4.CreateLookAtLeftHanded(new(4f, 3f, -6f), new(0f, 0.5f, 0f), System.Numerics.Vector3.UnitY),
        System.Numerics.Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 4f, 4f / 3f, 0.1f, 1000f),
        System.Numerics.Matrix4x4.Identity,
        System.Numerics.Matrix4x4.CreateTranslation(0f, 0.5f, 0f));

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
            Assert.IsTrue(view.Render(DefaultFrame));
            engine.WaitForGpu();
            var pixels = new byte[width * height * 4];
            view.ReadPixels(pixels, width * 4);

            CollectionAssert.AreEqual(BackgroundBgra, pixels[..4], "左上は背景色であること");
            int center = ((height / 2 * width) + (width / 2)) * 4;
            CollectionAssert.AreNotEqual(BackgroundBgra, pixels[center..(center + 4)], "中央には立方体が写ること");
        }
    }

    [TestMethod]
    public void SampleCountCanBeChangedFromCSharp()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 64;
        const int height = 48;
        using var view = engine.CreateOffscreenView(width, height);
        Assert.AreEqual(4, view.SetSampleCount(4), "WARP は 4 点を使える");
        Assert.IsTrue(view.Render(DefaultFrame));
        engine.WaitForGpu();
        var pixels = new byte[width * height * 4];
        view.ReadPixels(pixels, width * 4);
        CollectionAssert.AreEqual(BackgroundBgra, pixels[..4], "なめらかに描いても、まとめた面から読み出せる");

        int eight = view.SetSampleCount(8);
        Assert.IsTrue(eight is 4 or 8, $"使えなければ少ない数にする（{eight}）");
        var ex = Assert.ThrowsExactly<NativeEngineException>(() => view.SetSampleCount(3));
        Assert.AreEqual(NativeResult.InvalidArgument, ex.Result);
        Assert.AreEqual(1, view.SetSampleCount(1));
    }

    [TestMethod]
    public void PostEffectsReachTheEngine()
    {
        // 確認用の立方体（場面）は色調補正で暗くなり、背景はそのまま。周辺減光は背景の四隅も暗くする
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 64;
        const int height = 48;
        using var view = engine.CreateOffscreenView(width, height);
        byte[] Render(PostEffects effects)
        {
            Assert.IsTrue(view.Render(DefaultFrame with { Flags = FrameFlags.ShowTestObject, Effects = effects }));
            engine.WaitForGpu();
            var pixels = new byte[width * height * 4];
            view.ReadPixels(pixels, width * 4);
            return pixels;
        }
        int center = ((height / 2 * width) + (width / 2)) * 4;
        byte[] plain = Render(PostEffects.None);
        byte[] darker = Render(new PostEffects(PostEffectFlags.Color, Exposure: -1f));
        Assert.IsLessThan(plain[center + 1] * 0.6, (double)darker[center + 1], "立方体は半分ほどの明るさになる");
        CollectionAssert.AreEqual(BackgroundBgra, darker[..4], "背景には色調補正がかからない");
        byte[] vignette = Render(new PostEffects(PostEffectFlags.Vignette, Vignette: 1f));
        Assert.IsLessThan(BackgroundBgra[1] / 2, (int)vignette[1], "周辺減光は四隅の背景も暗くする");
    }

    [TestMethod]
    public void TexturedMeshIsDrawnFromCSharp()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 64;
        const int height = 48;
        using var view = engine.CreateOffscreenView(width, height);

        byte[] green = [0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255];
        NativeMesh mesh;
        using (NativeTexture texture = engine.CreateTexture(2, 2, green, 8))
        {
            var n = new System.Numerics.Vector3(0, 0, -1);
            MeshVertex[] quad =
            [
                new(new(-1, 1, 0), n, new(0, 0)),
                new(new(1, 1, 0), n, new(1, 0)),
                new(new(1, -1, 0), n, new(1, 1)),
                new(new(-1, -1, 0), n, new(0, 1)),
            ];
            uint[] indices = [0, 1, 2, 0, 2, 3]; // 正面から見て時計回り = 表
            MeshMaterial[] materials =
            [
                new(0, 6, System.Numerics.Vector4.One, System.Numerics.Vector3.Zero, 0, System.Numerics.Vector3.One, MaterialFlags.None, texture),
            ];
            mesh = engine.CreateMesh(quad, indices, materials);
        }

        using (mesh)
        {
            FrameParameters frame = DefaultFrame with
            {
                Flags = FrameFlags.None,
                View = System.Numerics.Matrix4x4.CreateLookAtLeftHanded(new(0, 0, -5), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY),
                Items = [new DrawItem(mesh, System.Numerics.Matrix4x4.Identity, DrawFlags.None)],
            };
            Assert.IsTrue(view.Render(frame));
            var pixels = new byte[width * height * 4];
            view.ReadPixels(pixels, width * 4);
            int center = ((height / 2 * width) + (width / 2)) * 4;
            Assert.IsLessThan(20, (int)pixels[center + 2], "赤は無い");
            Assert.IsGreaterThan(150, (int)pixels[center + 1], "テクスチャの緑が出る");
        }

        var ex = Assert.ThrowsExactly<NativeEngineException>(() => engine.CreateMesh(
            [new MeshVertex(default, default, default)], [0, 0, 5], []));
        Assert.AreEqual(NativeResult.InvalidArgument, ex.Result);
    }

    [TestMethod]
    public void ToonLightAndEdgeSettingsReachTheEngine()
    {
        // 構造体の並びが tt_engine.h と合っていること（合わなければ値が化けて、色や輪郭線が期待と違う）
        Assert.AreEqual(176, System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialState>());
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 32;
        const int height = 32;
        using var view = engine.CreateOffscreenView(width, height);

        byte[] toonPixels = [255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 200, 255, 0, 0, 200, 255]; // 上が白、下が赤
        var n = new System.Numerics.Vector3(0, 0, -1);
        MeshVertex[] quad =
        [
            new(new(-1, 1, 0), n, new(0, 0)),
            new(new(1, 1, 0), n, new(1, 0)),
            new(new(1, -1, 0), n, new(1, 1)),
            new(new(-1, -1, 0), n, new(0, 1)),
        ];
        NativeMesh mesh;
        using (NativeTexture toon = engine.CreateTexture(2, 2, toonPixels, 8))
        {
            MeshMaterial[] materials =
            [
                new(0, 6, System.Numerics.Vector4.One, System.Numerics.Vector3.Zero, 0, System.Numerics.Vector3.One, MaterialFlags.Edge, null,
                    ToonTexture: toon, EdgeSize: 1, EdgeColor: new(0, 1, 0, 1)),
            ];
            Assert.ThrowsExactly<ArgumentException>(() => engine.CreateMesh(quad, [0, 1, 2, 0, 2, 3], materials, [1f, 1f]), "輪郭線の倍率の数が頂点の数と違う");
            mesh = engine.CreateMesh(quad, [0, 1, 2, 0, 2, 3], materials, [1f, 1f, 1f, 1f]);
        }

        using (mesh)
        {
            byte[] RenderCenter(System.Numerics.Vector3 lightDirection)
            {
                FrameParameters frame = DefaultFrame with
                {
                    Flags = FrameFlags.UseLight,
                    View = System.Numerics.Matrix4x4.CreateLookAtLeftHanded(new(0, 0, -5), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY),
                    Items = [new DrawItem(mesh, System.Numerics.Matrix4x4.Identity, DrawFlags.None)],
                    LightDirection = lightDirection,
                    LightColor = new(0.6f),
                    EdgePixelScale = 1,
                };
                Assert.IsTrue(view.Render(frame));
                var pixels = new byte[width * height * 4];
                view.ReadPixels(pixels, width * 4);
                int center = ((height / 2 * width) + (width / 2)) * 4;
                return pixels[center..(center + 4)];
            }

            byte[] lit = RenderCenter(new(0, 0, 1));
            Assert.IsGreaterThan(240, (int)lit[1], "正面から照らすとトゥーンの上（白）");
            byte[] shadow = RenderCenter(new(0, 0, -1));
            Assert.IsLessThan(20, (int)shadow[1], "裏から照らすとトゥーンの下（赤）");
            Assert.IsGreaterThan(180, (int)shadow[2]);
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
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.Render(DefaultFrame));

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
    public void LostDeviceIsReportedAndRecoveredWithTheSameHandles()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        using var view = engine.CreateOffscreenView(16, 16);
        var events = new List<string>();
        engine.DeviceResetting += (_, _) => events.Add("前");
        engine.DeviceRestored += (_, _) => events.Add("後");
        Assert.IsFalse(engine.IsDeviceLost);

        engine.SimulateDeviceLost();

        Assert.IsTrue(engine.IsDeviceLost);
        var ex = Assert.ThrowsExactly<NativeEngineException>(() => view.Render(DefaultFrame));
        Assert.AreEqual(NativeResult.DeviceLost, ex.Result);

        engine.Recover();

        Assert.IsFalse(engine.IsDeviceLost);
        CollectionAssert.AreEqual(new[] { "前", "後" }, events);
        Assert.IsTrue(view.Render(DefaultFrame), "同じビューのまま描ける");
        engine.WaitForGpu();
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
