namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class VisualEffectsTests
{
    [TestMethod]
    public void NoneChangesNothing()
    {
        VisualEffects none = VisualEffects.None;
        Assert.IsFalse(none.Any);
        Assert.AreEqual(0f, none.Exposure);
        Assert.AreEqual(1f, none.Contrast);
        Assert.AreEqual(1f, none.Saturation);
        Assert.AreEqual(0f, none.Temperature);
        Assert.IsTrue((none with { VignetteEnabled = true }).Any);
        Assert.AreEqual(none, none.Normalized(), "初期値は範囲の中");
    }

    [TestMethod]
    public void ValuesAreKeptInRange()
    {
        var wild = new VisualEffects
        {
            Exposure = 10f,
            Contrast = -2f,
            Saturation = float.NaN,
            Temperature = -7f,
            BloomIntensity = float.PositiveInfinity,
            BloomThreshold = 2f,
            BloomSpread = -1f,
            Vignette = 0.5f,
        };
        VisualEffects fixedUp = wild.Normalized();
        Assert.AreEqual(VisualEffects.MaxExposure, fixedUp.Exposure);
        Assert.AreEqual(0f, fixedUp.Contrast);
        Assert.AreEqual(1f, fixedUp.Saturation, "数でない値は初期値");
        Assert.AreEqual(-1f, fixedUp.Temperature);
        Assert.AreEqual(0.6f, fixedUp.BloomIntensity, "無限大も初期値");
        Assert.AreEqual(1f, fixedUp.BloomThreshold);
        Assert.AreEqual(0f, fixedUp.BloomSpread);
        Assert.AreEqual(0.5f, fixedUp.Vignette);
    }

    [TestMethod]
    public void IllustrationValuesAreKeptInRange()
    {
        IllustrationStyle none = IllustrationStyle.None;
        Assert.IsFalse(none.Any);
        Assert.AreEqual(none, none.Normalized(), "初期値は範囲の中");
        Assert.IsTrue((none with { PaperEnabled = true }).Any);

        IllustrationStyle fixedUp = new IllustrationStyle
        {
            LineWidth = 100f,
            LineStrength = float.NaN,
            LineDetail = -1f,
            LineColor = "#abc",
            PaintLevels = 1,
            ShadowColor = "#00ff00",
            ShadowTintStrength = 2f,
            PaperStrength = -0.5f,
            PaperGrain = 0f,
        }.Normalized();
        Assert.AreEqual(IllustrationStyle.MaxLineWidth, fixedUp.LineWidth);
        Assert.AreEqual(none.LineStrength, fixedUp.LineStrength, "数でない値は初期値");
        Assert.AreEqual(0f, fixedUp.LineDetail);
        Assert.AreEqual(none.LineColor, fixedUp.LineColor, "#RRGGBB でない色は初期値");
        Assert.AreEqual(IllustrationStyle.MinPaintLevels, fixedUp.PaintLevels);
        Assert.AreEqual("#00FF00", fixedUp.ShadowColor);
        Assert.AreEqual(1f, fixedUp.ShadowTintStrength);
        Assert.AreEqual(0f, fixedUp.PaperStrength);
        Assert.AreEqual(IllustrationStyle.MinPaperGrain, fixedUp.PaperGrain);
        Assert.AreEqual(new System.Numerics.Vector3(0f, 1f, 0f), IllustrationStyle.ColorOf(fixedUp.ShadowColor));
    }

    [TestMethod]
    public void DepthOfFieldFocusesOnTheTargetByDefault()
    {
        VisualEffects dof = VisualEffects.None with { DofEnabled = true };
        Assert.IsTrue(dof.Any);
        Assert.AreEqual(30f, dof.FocusDistanceFor(30f), "初期値はカメラの注視点にピントを合わせる");
        Assert.AreEqual(12f, (dof with { DofFocusOnTarget = false, DofFocusDistance = 12f }).FocusDistanceFor(30f), "距離を指定するとき");
        Assert.AreEqual(15f, VisualEffects.DofTransitionFor(30f), "ぼけが最大になるまでの距離はピントの距離の半分");
        Assert.AreEqual(1f, VisualEffects.DofTransitionFor(0.5f), "近すぎるときも 1 より短くしない");

        VisualEffects wild = (dof with { DofFocusDistance = 0f, DofFocusRange = -3f, DofBlur = 500f }).Normalized();
        Assert.AreEqual(0.5f, wild.DofFocusDistance);
        Assert.AreEqual(0f, wild.DofFocusRange);
        Assert.AreEqual(VisualEffects.MaxDofBlur, wild.DofBlur);

        var settings = new RenderSettings
        {
            Effects = dof with { DofFocusOnTarget = false, DofFocusDistance = 12.5f, DofFocusRange = 2f, DofBlur = 9f },
        };
        ProjectFile read = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) }), new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Effects, restored.Effects, "被写界深度の値も保存して戻る");
    }

    [TestMethod]
    public void AmbientOcclusionIsKeptInRangeAndSaved()
    {
        Assert.IsFalse(VisualEffects.None.AoEnabled, "初期値は OFF");
        VisualEffects ao = VisualEffects.None with { AoEnabled = true };
        Assert.IsTrue(ao.Any);

        VisualEffects wild = (ao with { AoStrength = 5f, AoRadius = 0f }).Normalized();
        Assert.AreEqual(1f, wild.AoStrength);
        Assert.AreEqual(VisualEffects.MinAoRadius, wild.AoRadius);
        Assert.AreEqual(VisualEffects.None.AoRadius, (ao with { AoRadius = float.NaN }).Normalized().AoRadius, "数でない値は初期値");

        var settings = new RenderSettings { Effects = ao with { AoStrength = 0.4f, AoRadius = 2.5f } };
        ProjectFile read = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) }), new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Effects, restored.Effects, "環境光の遮りの値も保存して戻る");
    }

    [TestMethod]
    public void ScreenReflectionsAreKeptInRangeAndSaved()
    {
        Assert.IsFalse(VisualEffects.None.SsrEnabled, "初期値は OFF");
        VisualEffects ssr = VisualEffects.None with { SsrEnabled = true };
        Assert.IsTrue(ssr.Any);

        VisualEffects wild = (ssr with { SsrStrength = 5f, SsrDistance = 0f }).Normalized();
        Assert.AreEqual(1f, wild.SsrStrength);
        Assert.AreEqual(VisualEffects.MinSsrDistance, wild.SsrDistance);
        Assert.AreEqual(VisualEffects.MaxSsrDistance, (ssr with { SsrDistance = 1e6f }).Normalized().SsrDistance);
        Assert.AreEqual(VisualEffects.None.SsrStrength, (ssr with { SsrStrength = float.NaN }).Normalized().SsrStrength, "数でない値は初期値");

        Assert.IsFalse(ssr.SsrRayTracing, "レイトレーシングの初期値は OFF");
        var settings = new RenderSettings { Effects = ssr with { SsrStrength = 0.7f, SsrDistance = 45f, SsrRayTracing = true } };
        ProjectFile read = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) }), new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Effects, restored.Effects, "画面の中の反射の値（レイトレーシングを使うかも）も保存して戻る");
    }

    [TestMethod]
    public void EnvironmentIsKeptInRangeAndSavedRelativeToTheProject()
    {
        Assert.IsFalse(SceneEnvironment.Off.IsActive, "初期値は使わない");
        Assert.IsFalse((SceneEnvironment.Off with { Enabled = true }).IsActive, "ファイルが無ければ使わない");
        SceneEnvironment wild = new SceneEnvironment { Enabled = true, File = "  ", Intensity = 9f, Rotation = 270f }.Normalized();
        Assert.IsNull(wild.File, "空のファイル名は無し");
        Assert.AreEqual(SceneEnvironment.MaxIntensity, wild.Intensity);
        Assert.AreEqual(-90f, wild.Rotation, "回転は -180〜180 に回し込む");
        Assert.AreEqual(MathF.PI / 2f, (SceneEnvironment.Off with { Rotation = 90f }).RotationRadians, 1e-6f);

        // プロジェクトのフォルダーの中の画像は、相対で書き、フォルダーごと動かしても見つかる
        string root = Path.Combine(Path.GetTempPath(), "3dt_env_" + Guid.NewGuid().ToString("N"));
        string before = Path.Combine(root, "before");
        string after = Path.Combine(root, "after");
        Directory.CreateDirectory(Path.Combine(before, "hdri"));
        try
        {
            string image = Path.Combine(before, "hdri", "sky.hdr");
            File.WriteAllText(image, "#?RADIANCE");
            var settings = new RenderSettings
            {
                Environment = new SceneEnvironment { Enabled = true, File = image, Intensity = 1.5f, Rotation = 30f, ShowBackground = false },
            };
            string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(new RenderSettings(), before) });
            Assert.IsFalse(plain.Contains("environment", StringComparison.OrdinalIgnoreCase), "使っていなければ書かない");
            string json = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings, before) });
            StringAssert.Contains(json, "hdri\\\\sky.hdr", "フォルダーから見た場所で書く");

            Directory.Move(before, after);
            ProjectFile read = ProjectSerializer.Deserialize(json, new Formats.LoadReport())!;
            var restored = new RenderSettings();
            read.Rendering.ApplyTo(restored, after);
            Assert.AreEqual(Path.Combine(after, "hdri", "sky.hdr"), restored.Environment.File, "動かした先で見つける");
            Assert.AreEqual(settings.Environment with { File = restored.Environment.File }, restored.Environment);

            restored.Reset();
            Assert.AreEqual(SceneEnvironment.Off, restored.Environment, "新しいプロジェクトは環境の画像なし");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ShadowStyleIsKeptInRangeAndSaved()
    {
        Assert.AreEqual(0f, ShadowStyle.Default.SoftnessTangent, "初期値は、今までどおりのくっきりした影");
        Assert.AreEqual(new System.Numerics.Vector4(0f, 0f, 0f, 0.5f), ShadowStyle.Default.GroundColor, "地面の影は半透明の黒");
        Assert.AreEqual(ShadowStyle.MaxSoftnessTangent, (ShadowStyle.Default with { Softness = 1f }).SoftnessTangent);

        ShadowStyle wild = new ShadowStyle { Softness = 3f, GroundOpacity = float.NaN }.Normalized();
        Assert.AreEqual(1f, wild.Softness);
        Assert.AreEqual(0.5f, wild.GroundOpacity, "数でない値は初期値");

        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.ShadowStyle = new ShadowStyle { Softness = 0.4f, GroundOpacity = 0.3f };
        settings.ShadowStyle = new ShadowStyle { Softness = 0.4f, GroundOpacity = 0.3f };
        Assert.AreEqual(1, changes, "同じ値では知らせない");

        string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(new RenderSettings()) });
        Assert.IsFalse(plain.Contains("shadowStyle", StringComparison.Ordinal), "初期のままなら書かない");
        string saved = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) });
        Assert.IsFalse(saved.Contains("softnessTangent", StringComparison.Ordinal), "計算で出す値は書かない");
        ProjectFile read = ProjectSerializer.Deserialize(saved, new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.ShadowStyle, restored.ShadowStyle);

        restored.Reset();
        Assert.AreEqual(ShadowStyle.Default, restored.ShadowStyle, "新しいプロジェクトは初期の見え方");
    }

    [TestMethod]
    public void FogIsKeptInRangeAndSaved()
    {
        Assert.IsFalse(SceneFog.Off.Enabled, "初期値は OFF");
        Assert.AreEqual(0f, SceneFog.Off.HeightScale, "地面の近くだけでなければ、高さに関係なく同じ濃さ");
        Assert.AreEqual(8f, (SceneFog.Off with { GroundOnly = true }).HeightScale);
        Assert.AreEqual(0.95f, 1f - MathF.Exp(-SceneFog.Off.Density * SceneFog.Off.Visibility), 0.001f, "見通せる距離で 95% が霧の色");

        SceneFog wild = new SceneFog { Enabled = true, Color = "まちがい", Start = -5f, Visibility = 0f, Height = float.NaN }.Normalized();
        Assert.AreEqual(SceneFog.Off.Color, wild.Color, "読めない色は初期値");
        Assert.AreEqual(0f, wild.Start);
        Assert.AreEqual(SceneFog.MinVisibility, wild.Visibility);
        Assert.AreEqual(SceneFog.Off.Height, wild.Height, "数でない値は初期値");
        Assert.AreEqual("#FF8000", (SceneFog.Off with { Color = " #ff8000 " }).Normalized().Color, "色は大文字の #RRGGBB にそろえる");

        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.Fog = SceneFog.Off with { Enabled = true, Color = "#102030", GroundOnly = true, Height = 3f };
        settings.Fog = SceneFog.Off with { Enabled = true, Color = "#102030", GroundOnly = true, Height = 3f };
        Assert.AreEqual(1, changes, "同じ値では知らせない");

        string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(new RenderSettings()) });
        Assert.IsFalse(plain.Contains("fog", StringComparison.OrdinalIgnoreCase), "使っていなければ書かない");
        string saved = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) });
        Assert.IsFalse(saved.Contains("density", StringComparison.OrdinalIgnoreCase), "計算で出す値は書かない");
        ProjectFile read = ProjectSerializer.Deserialize(saved, new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Fog, restored.Fog);

        restored.Reset();
        Assert.AreEqual(SceneFog.Off, restored.Fog, "新しいプロジェクトは霧なし");
    }

    [TestMethod]
    public void FloorReflectionIsKeptInRangeAndSaved()
    {
        Assert.IsFalse(FloorReflection.Off.Enabled);
        FloorReflection wild = new FloorReflection { Enabled = true, Strength = 3f, Fade = float.NaN }.Normalized();
        Assert.AreEqual(1f, wild.Strength);
        Assert.AreEqual(FloorReflection.Off.Fade, wild.Fade, "数でない値は初期値");
        Assert.AreEqual(FloorReflection.MinFade, (FloorReflection.Off with { Fade = 0f }).Normalized().Fade);
        Assert.AreEqual(0f, FloorReflection.Off.Blur, "初めはくっきり（前の版のファイルも）");
        Assert.AreEqual(1f, (FloorReflection.Off with { Blur = 5f }).Normalized().Blur);
        Assert.AreEqual(0f, (FloorReflection.Off with { Blur = -1f }).Normalized().Blur);
        Assert.AreEqual(0f, (FloorReflection.Off with { Blur = float.NaN }).Normalized().Blur, "数でない値はくっきり");

        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.Reflection = FloorReflection.Off with { Enabled = true, Strength = 0.5f, Blur = 0.4f };
        settings.Reflection = FloorReflection.Off with { Enabled = true, Strength = 0.5f, Blur = 0.4f };
        Assert.AreEqual(1, changes, "同じ値では知らせない");

        string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(new RenderSettings()) });
        Assert.IsFalse(plain.Contains("reflection", StringComparison.OrdinalIgnoreCase), "使っていなければ書かない");
        ProjectFile read = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) }), new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Reflection, restored.Reflection);
        Assert.AreEqual(0.4f, restored.Reflection.Blur, "ぼかしも保存する");

        restored.Reset();
        Assert.AreEqual(FloorReflection.Off, restored.Reflection, "新しいプロジェクトは映り込みなし");
    }

    [TestMethod]
    public void RenderSettingsNotifyOnlyWhenEffectsChange()
    {
        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        Assert.AreEqual(VisualEffects.None, settings.Effects);

        settings.Effects = VisualEffects.None with { ColorEnabled = true, Saturation = 0f };
        settings.Effects = VisualEffects.None with { ColorEnabled = true, Saturation = 0f };
        Assert.AreEqual(1, changes, "同じ値（別の入れ物）では知らせない");

        settings.Effects = settings.Effects with { Exposure = 99f };
        Assert.AreEqual(VisualEffects.MaxExposure, settings.Effects.Exposure, "範囲に収めて持つ");
        Assert.AreEqual(2, changes);
        settings.Effects = settings.Effects with { Exposure = 50f };
        Assert.AreEqual(2, changes, "収めた結果が同じなら知らせない");

        settings.Illustration = IllustrationStyle.None with { LinesEnabled = true };
        Assert.AreEqual(3, changes, "イラスト風も変わったら知らせる");
        settings.Illustration = IllustrationStyle.None with { LinesEnabled = true, LineColor = "#2b2222" };
        Assert.AreEqual(3, changes, "そろえると同じ色なら知らせない");

        settings.Reset();
        Assert.AreEqual(VisualEffects.None, settings.Effects, "新しいプロジェクトは映像効果なし");
        Assert.AreEqual(IllustrationStyle.None, settings.Illustration, "イラスト風もなし");
        Assert.AreEqual(5, changes, "映像効果とイラスト風の 2 回");
    }
}
