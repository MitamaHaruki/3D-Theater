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

        // イラスト風の値も届く: 線画（赤）が立方体の輪郭に引かれる
        byte[] lines = Render(new PostEffects(PostEffectFlags.Lines, LineColor: new System.Numerics.Vector3(1f, 0f, 0f), LineWidth: 1f,
            LineStrength: 1f, LineDetail: 0.5f));
        int red = 0;
        for (int i = 0; i < lines.Length; i += 4)
        {
            red += lines[i + 2] > 200 && lines[i + 1] < 80 && lines[i] < 80 ? 1 : 0;
        }
        Assert.IsGreaterThan(10, red, "立方体の輪郭に赤い線");
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
        Assert.AreEqual(208, System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialState>());
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
    public void ExtraLightShadowsReachTheEngine()
    {
        // 構造体の並びが tt_engine.h と合っていること
        Assert.AreEqual(64, System.Runtime.CompilerServices.Unsafe.SizeOf<TtLight>());
        // 床（影を受ける）と、その上の板（影を落とす）を真上から見て、左上の点光源だけで照らす。影を落とす照明は、
        // 影をレイトレーシングで求めるとき、床の右に板の影を落とす
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 64;
        const int height = 48;
        using var view = engine.CreateOffscreenView(width, height);
        var up = System.Numerics.Vector3.UnitY;
        NativeMesh Plate(float y, float half, MaterialFlags flags)
        {
            MeshVertex[] quad =
            [
                new(new(-half, y, half), up, new(0, 0)),
                new(new(half, y, half), up, new(1, 0)),
                new(new(half, y, -half), up, new(1, 1)),
                new(new(-half, y, -half), up, new(0, 1)),
            ];
            MeshMaterial[] materials =
            [
                new(0, 6, System.Numerics.Vector4.One, System.Numerics.Vector3.Zero, 0, System.Numerics.Vector3.Zero,
                    flags | MaterialFlags.DoubleSided, null),
            ];
            return engine.CreateMesh(quad, [0, 1, 2, 0, 2, 3], materials);
        }

        using NativeMesh floor = Plate(0f, 3f, MaterialFlags.ReceiveShadow);
        using NativeMesh occluder = Plate(1f, 0.5f, MaterialFlags.CastShadow);
        byte RenderGreen(bool castsShadow, FrameFlags flags, int x)
        {
            FrameParameters frame = DefaultFrame with
            {
                Flags = FrameFlags.UseLight | flags,
                View = System.Numerics.Matrix4x4.CreateLookAtLeftHanded(new(0, 8, 0), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ),
                Items = [new DrawItem(floor, System.Numerics.Matrix4x4.Identity, DrawFlags.None),
                    new DrawItem(occluder, System.Numerics.Matrix4x4.Identity, DrawFlags.None)],
                LightDirection = -up,
                LightColor = System.Numerics.Vector3.Zero, // メインの照明は使わない
                Lights = [new LightSource(LightSourceKind.Point, new(-3f, 3f, 0f), default, 20f, System.Numerics.Vector3.One,
                    CastsShadow: castsShadow)],
            };
            Assert.IsTrue(view.Render(frame));
            var pixels = new byte[width * height * 4];
            view.ReadPixels(pixels, width * 4);
            return pixels[(((height / 2) * width) + x) * 4 + 1];
        }

        const FrameFlags rayTraced = FrameFlags.SelfShadow | FrameFlags.RayTracedShadows;
        Assert.IsLessThan(10, (int)RenderGreen(true, rayTraced, 43), "影を落とす照明は、板の影を落とす");
        Assert.IsGreaterThan(60, (int)RenderGreen(true, rayTraced, 21), "影の外は照明で明るい");
        Assert.IsGreaterThan(40, (int)RenderGreen(false, rayTraced, 43), "影を落とさない照明は影を落とさない");
        Assert.IsGreaterThan(40, (int)RenderGreen(true, FrameFlags.SelfShadow, 43), "レイトレーシングでなければ、足す照明は影を落とさない");
    }

    [TestMethod]
    public void RenderPassesReachTheEngine()
    {
        // カメラの 5 先に、カメラへ向いた四角形を 1 つ置き、パスごとに真ん中の画素を読む
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 32;
        const int height = 32;
        using var view = engine.CreateOffscreenView(width, height);
        var n = new System.Numerics.Vector3(0, 0, -1);
        MeshVertex[] quad =
        [
            new(new(-1, 1, 0), n, new(0, 0)),
            new(new(1, 1, 0), n, new(1, 0)),
            new(new(1, -1, 0), n, new(1, 1)),
            new(new(-1, -1, 0), n, new(0, 1)),
        ];
        MeshMaterial[] materials = [new(0, 6, System.Numerics.Vector4.One, System.Numerics.Vector3.Zero, 0, System.Numerics.Vector3.One, MaterialFlags.None, null)];
        using NativeMesh mesh = engine.CreateMesh(quad, [0, 1, 2, 0, 2, 3], materials);
        byte[] Render(RenderPass pass, out byte[] corner)
        {
            FrameParameters frame = DefaultFrame with
            {
                Flags = FrameFlags.UseLight | FrameFlags.ClearColor,
                View = System.Numerics.Matrix4x4.CreateLookAtLeftHanded(new(0, 0, -5), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY),
                Items = [new DrawItem(mesh, System.Numerics.Matrix4x4.Identity, DrawFlags.None)],
                ClearColor = System.Numerics.Vector4.Zero,
                Pass = pass,
                PassDepthNear = 0f,
                PassDepthFar = 10f,
            };
            Assert.IsTrue(view.Render(frame));
            var pixels = new byte[width * height * 4];
            view.ReadPixels(pixels, width * 4);
            corner = pixels[0..4];
            int center = ((height / 2 * width) + (width / 2)) * 4;
            return pixels[center..(center + 4)];
        }

        var objectColor = RenderPassColors.ObjectColor(1);
        byte[] id = Render(RenderPass.ObjectId, out byte[] background);
        CollectionAssert.AreEqual(new byte[] { objectColor.B, objectColor.G, objectColor.R, 255 }, id, "物の番号の色は、C# の式と 1 の位まで同じ");
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, background, "何も無い所は透明な黒");

        var materialColor = RenderPassColors.MaterialColor(1, 0);
        CollectionAssert.AreEqual(new byte[] { materialColor.B, materialColor.G, materialColor.R, 255 }, Render(RenderPass.MaterialId, out _));
        CollectionAssert.AreNotEqual(id, Render(RenderPass.MaterialId, out _), "物の色と材質の色は違う");

        byte[] depth = Render(RenderPass.Depth, out _);
        int packed = (depth[2] << 8) | depth[1]; // 赤が上位、緑が下位
        Assert.AreEqual(0.5, packed / 65535.0, 0.0002, "カメラから 5 先は、0〜10 の真ん中");

        byte[] normal = Render(RenderPass.Normal, out _);
        Assert.AreEqual(128, normal[2], 1, "カメラへ向いた面は、右と上の成分が 0（真ん中の 128）");
        Assert.AreEqual(128, normal[1], 1);
        Assert.IsLessThan(2, (int)normal[0], "奥の成分は -1（手前を向く）");

        byte[] alpha = Render(RenderPass.Alpha, out byte[] alphaBackground);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, alpha, "アルファのパスは不透明な白");
        Assert.AreEqual(0, alphaBackground[3]);

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 255 }, Render(RenderPass.Emission, out _), "発光の無い材質は黒");
    }

    [TestMethod]
    public void IdColorsAreBrightEnoughAndDiffer()
    {
        var colors = Enumerable.Range(1, 200).Select(i => RenderPassColors.ObjectColor(i)).ToList();
        Assert.IsTrue(colors.All(c => c.R >= 64 && c.G >= 64 && c.B >= 64), "黒い背景と区別できる");
        Assert.HasCount(200, colors.Distinct(), "番号ごとに違う色");
        Assert.AreEqual(RenderPassColors.IdColor(4097), RenderPassColors.MaterialColor(1, 0));
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
    public void DeferredViewShowsOnlyPublishedFrames()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        const int width = 32;
        const int height = 24;
        using var view = engine.CreateOffscreenView(width, height, deferredPublish: true);
        var pixels = new byte[width * height * 4];
        byte[] Read()
        {
            engine.WaitForGpu();
            view.ReadPixels(pixels, width * 4);
            return pixels[..4];
        }
        FrameParameters Clear(float r, float g, float b) =>
            DefaultFrame with { Flags = FrameFlags.ClearColor, ClearColor = new System.Numerics.Vector4(r, g, b, 1f) };

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Read(), "作ったときの見える面は透明な黒");
        Assert.IsTrue(view.Render(Clear(1f, 0f, 0f)));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Read(), "描いただけでは、見える面は変わらない");
        view.Publish();
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, Read(), "写すと、描いた絵が見える");
        Assert.IsTrue(view.Render(Clear(0f, 1f, 0f)));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, Read(), "次の絵を描いている間も、前に写した絵が見えている");
        view.Publish();
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, Read(), "写すと、次の絵に変わる");
    }

    [TestMethod]
    public void GpuTimeIsReadWithoutWaiting()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        using var view = engine.CreateOffscreenView(32, 24);
        Assert.IsNull(view.GpuMilliseconds, "まだ描いていなければ測れていない");
        float? measured = null;
        for (int i = 0; i < 4 && measured is null; i++)
        {
            Assert.IsTrue(view.Render(DefaultFrame));
            engine.WaitForGpu();
            measured = view.GpuMilliseconds;
        }
        Assert.IsNotNull(measured, "描いて GPU が終えれば、時間が読める");
        Assert.IsGreaterThanOrEqualTo(0f, measured.Value);

        // 同じ回の、段ごとの時間（影の下準備・場面・映像効果・重ね描き・まとめ）。足すと全体になる
        float[]? stages = view.GpuBreakdown;
        Assert.IsNotNull(stages, "全体が測れれば、段ごとの時間も読める");
        Assert.HasCount(NativeView.GpuStageCount, stages);
        foreach (float stage in stages)
        {
            Assert.IsGreaterThanOrEqualTo(0f, stage);
        }
        Assert.AreEqual(measured.Value, stages.Sum(), 0.01 + measured.Value * 0.01, "段の時間を足すと、全体の時間");
        Assert.AreEqual(0f, stages[2], "映像効果を使わなければ、効果の段は 0");
    }

    [TestMethod]
    public void GpuBreakdownIsNotReadyBeforeRendering()
    {
        using var engine = NativeEngine.Create(NativeEngineOptions.UseWarp);
        using var view = engine.CreateOffscreenView(16, 16);
        Assert.IsNull(view.GpuBreakdown, "まだ描いていなければ測れていない");
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
