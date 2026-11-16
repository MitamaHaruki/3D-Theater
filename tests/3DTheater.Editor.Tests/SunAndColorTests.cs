using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

/// <summary>サンライトと時刻・色のパレット・数の欄のドラッグ（設計判断記録 87 章）。</summary>
[TestClass]
public sealed class SunAndColorTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, RenderSettings Settings, UndoService Undo,
        EditService Edits) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var settings = new RenderSettings();
        animation.Rendering = settings;
        var undo = new UndoService();
        return (scene, playback, animation, settings, undo, new EditService(undo, animation, playback));
    }

    [TestMethod]
    public void MainLightIsTheSun()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        Assert.AreEqual("サンライト", scene.MainLight.Name, "MMD の照明は、初めからサンライト");

        // 前の版の初めの名前のままのファイルはサンライトに、自分で付けた名前はそのまま
        var (scene2, playback, animation, _) = AnimationFixtures.Create();
        ProjectFile old = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"nodes":{"light":{"name":"ディレクショナルライト","visible":true}}}""", new LoadReport())!;
        ProjectApply.ApplyScene(scene2, playback, animation, old, new LoadReport());
        Assert.AreEqual(SceneDocument.MainLightName, scene2.MainLight.Name);
        ProjectFile renamed = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"nodes":{"light":{"name":"窓の光","visible":true}}}""", new LoadReport())!;
        ProjectApply.ApplyScene(scene2, playback, animation, renamed, new LoadReport());
        Assert.AreEqual("窓の光", scene2.MainLight.Name);
    }

    [TestMethod]
    public void TimeOfDaySetsTheSunAndTheColorsAtOnce()
    {
        var (scene, _, animation, settings, undo, edits) = Create();
        LightNode sun = scene.MainLight;
        TimeOfDayNode time = scene.TimeOfDay;
        Vector3 original = sun.Rotation;
        sun.IsVisible = false;
        settings.SelfShadow = false;
        settings.Effects = settings.Effects with { BloomEnabled = true };

        TimeOfDayPresets.Apply(edits, animation, settings, TimeOfDay.Evening);
        Assert.AreEqual(17f, time.Hour, "夕は 17 時");
        SkyPosition evening = SunClock.PositionAt(17f);
        Assert.AreEqual(evening.Elevation, sun.Rotation.X, 1e-4f, "太陽の高さ");
        Assert.AreEqual(12.2f, sun.Rotation.X, 0.1f, "夕の太陽は低い");
        Assert.AreEqual(TimeOfDayNode.DefaultNoonAzimuth + evening.Azimuth, sun.Rotation.Y, 1e-4f, "太陽の向きも、昼より右（西）へ回る");
        Assert.IsGreaterThan(60f, evening.Azimuth);
        Assert.AreEqual(SunClock.Evening.Color, sun.Color);
        Assert.AreEqual(SunClock.Evening.Intensity, sun.Intensity);
        Assert.IsTrue(sun.IsVisible, "太陽を出す");
        Assert.IsTrue(settings.SelfShadow, "太陽は影を落とす");
        Assert.IsFalse(settings.Effects.ColorEnabled, "映像効果の値は変えない（色合いは描くときに重ねる）");
        Assert.IsTrue(settings.Effects.BloomEnabled, "ほかの映像効果はそのまま");
        Assert.AreEqual(SunClock.Evening.Grade, SunClock.GradeFor(settings.Effects, time), "描くときは夕の色合い");
        Assert.IsLessThan(0f, SunClock.Night.Grade.Exposure, "夜は暗い");
        Assert.IsLessThan(0f, SunClock.Night.Grade.Temperature, "夜は青っぽい");

        // 元に戻すは 1 回で、時刻も太陽も戻る（色合いは時刻から決まるので、一緒に戻る）
        Assert.AreEqual("時刻を「夕」に", undo.UndoDescription);
        undo.Undo();
        Assert.AreEqual(TimeOfDayNode.DefaultHour, time.Hour);
        Assert.AreEqual(original, sun.Rotation);
        Assert.IsFalse(sun.IsVisible);
        Assert.IsFalse(settings.SelfShadow);
        Assert.IsTrue(SunClock.GradeFor(settings.Effects, time).IsNeutral);
        Assert.IsFalse(undo.CanUndo, "時刻の変更は 1 つの操作");
        undo.Redo();
        Assert.AreEqual(17f, time.Hour);
        Assert.AreEqual(evening.Elevation, sun.Rotation.X, 1e-4f);

        // 昼は MMD の初めの照明と同じで、色合いを変えない
        TimeOfDayPresets.Apply(edits, animation, settings, TimeOfDay.Noon);
        Assert.AreEqual(SceneLighting.MmdRotation.X, sun.Rotation.X, 1e-3f);
        Assert.AreEqual(SceneLighting.MmdRotation.Y, sun.Rotation.Y, 1e-3f);
        Assert.AreEqual(Vector3.One, sun.Color);
        Assert.AreEqual(1f, sun.Intensity);
        Assert.IsTrue(SunClock.GradeFor(settings.Effects, time).IsNeutral, "昼は色合いを変えない");

        // 時刻の欄を続けて変えると、1 回の元に戻すにまとまる
        int before = undo.UndoCount;
        for (float h = 12.25f; h <= 14f; h += 0.25f)
        {
            TimeOfDayPresets.SetHour(edits, animation, settings, h, "時刻", mergeKey: "time:hour");
        }
        Assert.AreEqual(before + 1, undo.UndoCount);
        Assert.AreEqual(14f, time.Hour);
        undo.Undo();
        Assert.AreEqual(12f, time.Hour);
    }

    [TestMethod]
    public void SunAndMoonFollowTheSky()
    {
        SkyPosition noon = SunClock.PositionAt(12f);
        Assert.AreEqual(SceneLighting.MmdRotation.X, noon.Elevation, 1e-3f, "昼の高さは MMD の初めの照明");
        Assert.AreEqual(0f, noon.Azimuth, 1e-4f);
        Assert.IsFalse(noon.IsMoon);
        Assert.AreEqual(-90f, SunClock.PositionAt(6f).Azimuth, 1e-3f, "6 時に左（東）から昇る");
        Assert.AreEqual(90f, SunClock.PositionAt(18f).Azimuth, 1e-3f, "18 時に右（西）へ沈む");
        Assert.AreEqual(SunClock.MinElevation, SunClock.PositionAt(6f).Elevation, "地平線すれすれにはしない");
        Assert.AreEqual(24f, SunClock.PositionAt(8f).Elevation, 0.2f, "朝 8 時");
        Assert.AreEqual(-SunClock.PositionAt(9f).Azimuth, SunClock.PositionAt(15f).Azimuth, 1e-3f, "昼をはさんで左右に同じだけ");
        Assert.AreEqual(SunClock.PositionAt(9f).Elevation, SunClock.PositionAt(15f).Elevation, 1e-3f);

        SkyPosition night = SunClock.PositionAt(22f);
        Assert.IsTrue(night.IsMoon, "夜は月");
        Assert.AreEqual(45f, night.Elevation, 0.1f, "22 時の月の高さ");
        Assert.AreEqual(-45f, night.Azimuth, 0.1f, "22 時の月は左寄り（南東）");
        Assert.AreEqual(SceneLighting.MmdRotation.X, SunClock.PositionAt(0f).Elevation, 1e-3f, "0 時の月がいちばん高い");
        Assert.AreEqual(SunClock.PositionAt(12f), SunClock.PositionAt(36f), "36 時は次の日の 12 時");

        // 光の色・明るさ・色合いは、決めた時刻ではその値、間はなめらかに
        Assert.AreEqual(SunClock.Morning, SunClock.LookAt(8f));
        Assert.AreEqual(SunClock.Noon, SunClock.LookAt(12f));
        Assert.AreEqual(SunClock.Evening, SunClock.LookAt(17f));
        Assert.AreEqual(SunClock.Night, SunClock.LookAt(22f));
        Assert.AreEqual(SunClock.Night, SunClock.LookAt(2f));
        Assert.AreEqual(SunClock.Twilight, SunClock.LookAt(18f), "日の入りは光を弱くして、太陽と月を入れ替える");
        Assert.IsLessThan(SunClock.Night.Intensity, SunClock.Twilight.Intensity);
        float afternoon = SunClock.LookAt(16f).Intensity;
        Assert.IsTrue(afternoon > 1f && afternoon < 1.1f, "15〜17 時は昼から夕へ");

        // 太陽の向きを動かさないときは、いつも昼の向き
        var time = new TimeOfDayNode { Hour = 17f, NoonAzimuth = 120f, FollowsSunPath = false };
        Assert.AreEqual(120f, SunClock.SunRotation(time).Y);
        time.FollowsSunPath = true;
        Assert.AreEqual(120f + SunClock.PositionAt(17f).Azimuth, SunClock.SunRotation(time).Y, 1e-4f);
        time.Hour = 100f;
        Assert.AreEqual(TimeOfDayNode.MaxHour, time.Hour, "48 時まで");
        time.Hour = float.NaN;
        Assert.AreEqual(TimeOfDayNode.MaxHour, time.Hour, "数でない値は入れない");

        Assert.AreEqual("17:30", SunClock.FormatClock(17.5f));
        Assert.AreEqual("8:15", SunClock.FormatClock(8.25f));
        Assert.AreEqual("25:00（次の日の 1:00）", SunClock.FormatClock(25f));

        // 色合いの重ね方: 明るさ・色温度は足し、コントラスト・彩度は掛ける
        var user = new ColorGrade(0.5f, 1.2f, 0.5f, 0.1f);
        Assert.AreEqual(user, user.Then(ColorGrade.Neutral));
        Assert.AreEqual(user, ColorGrade.Neutral.Then(user));
        ColorGrade both = user.Then(new ColorGrade(-1f, 1.5f, 2f, 0.95f));
        Assert.AreEqual(-0.5f, both.Exposure, 1e-5f);
        Assert.AreEqual(1.8f, both.Contrast, 1e-5f);
        Assert.AreEqual(1f, both.Saturation, 1e-5f);
        Assert.AreEqual(1f, both.Temperature, "範囲に収める");
        var effects = new VisualEffects { ColorEnabled = false, Exposure = 2f };
        Assert.IsTrue(ColorGrade.Of(effects).IsNeutral, "使っていない色調補正は重ねない");
    }

    [TestMethod]
    public void TimeKeysMoveTheSunAlongTheDay()
    {
        var (scene, playback, animation, settings, _, edits) = Create();
        TimeOfDayNode time = scene.TimeOfDay;
        LightNode sun = scene.MainLight;
        playback.EndFrame = 200;

        // 0 フレーム目に朝 8 時、100 フレーム目に夕 17 時のキー
        TimeOfDayPresets.SetHour(edits, animation, settings, 8f, "時刻");
        animation.Register([time]);
        playback.Seek(100);
        TimeOfDayPresets.SetHour(edits, animation, settings, 17f, "時刻");
        Assert.IsTrue(time.HasUnregisteredChange, "キーのあるときに時刻を変えると、未登録の変更");
        animation.Register([time]);
        Assert.IsFalse(time.HasUnregisteredChange);
        Assert.AreEqual(2, time.Keys.Count);

        // キーの間は時刻が進み、太陽は昼の高い所を通る（向き・高さを直線でつながない）
        playback.Seek(50);
        Assert.AreEqual(12.5f, time.Hour, 1e-4f);
        Assert.AreEqual(SunClock.SunRotation(time), sun.Rotation);
        Assert.IsGreaterThan(50f, sun.Rotation.X, "昼ごろは高い");
        Assert.AreEqual(SunClock.LookAt(12.5f).Color, sun.Color);
        Assert.AreEqual(12.5f, CurveEditing.Sample(time, 0, 100).Single().Values[50], 1e-4f, "グラフの時刻");
        Assert.AreEqual("時刻", CurveEditing.ChannelNames(time).Single());

        // 時刻のキーがあるときに、サンライトを手で変えると未登録の変更（黙って時刻の値に戻さない）
        sun.Color = new Vector3(1f, 0f, 0f);
        Assert.IsTrue(sun.HasUnregisteredChange);
        playback.Seek(60);
        Assert.AreEqual(new Vector3(1f, 0f, 0f), sun.Color, "未登録の変更は保つ");
        Assert.AreEqual(13.4f, time.Hour, 1e-4f, "時刻は進む");
        animation.DiscardUnregistered();
        Assert.IsFalse(sun.HasUnregisteredChange);
        Assert.AreEqual(SunClock.LookAt(13.4f).Color, sun.Color, "捨てると時刻の光に戻る");

        // 時刻のボタンは、サンライトの未登録の変更を消して時刻に従わせる（時刻は未登録の変更）
        sun.Intensity = 3f;
        TimeOfDayPresets.Apply(edits, animation, settings, TimeOfDay.Night);
        Assert.IsFalse(sun.HasUnregisteredChange);
        Assert.IsTrue(time.HasUnregisteredChange);
        Assert.AreEqual(SunClock.Night.Intensity, sun.Intensity);
        Assert.IsGreaterThan(30f, sun.Rotation.X, "22 時の月");
        playback.Seek(0);
        Assert.AreEqual(22f, time.Hour, "未登録の時刻は保つ");
        animation.DiscardUnregistered();
        Assert.AreEqual(8f, time.Hour);

        // キーの移動・コピーと貼り付け・削除・補間
        IReadOnlyList<KeyRef> moved = KeyEditing.Move([new KeyRef(time, 100)], 20, duplicate: false);
        Assert.AreEqual(120, moved.Single().Frame);
        KeyEditing.Paste(KeyEditing.Copy([new KeyRef(time, 0)]), 150);
        Assert.AreEqual(3, KeyEditing.CountKeys(time));
        KeyEditing.SetInterpolation([new KeyRef(time, 120)], InterpolationPreset.Smooth);
        Assert.IsTrue(CurveEditing.TryGetCurve(new KeyRef(time, 120), 0, out BezierCurve curve, out _));
        Assert.AreEqual(KeyEditing.SmoothCurve, curve);
        Assert.AreEqual(1, KeyEditing.Delete([new KeyRef(time, 150)]));
        Assert.HasCount(2, KeyEditing.InRange([time], 0, 200));

        // 保存して開き直しても同じ（時刻・昼の向き・向きを動かすか・キー）
        time.NoonAzimuth = 30f;
        time.FollowsSunPath = false;
        string json = ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, []));
        var (scene2, playback2, animation2, _) = AnimationFixtures.Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, ProjectSerializer.Deserialize(json, new LoadReport())!, new LoadReport());
        Assert.AreEqual(time.Hour, scene2.TimeOfDay.Hour);
        Assert.AreEqual(30f, scene2.TimeOfDay.NoonAzimuth);
        Assert.IsFalse(scene2.TimeOfDay.FollowsSunPath);
        CollectionAssert.AreEqual(time.Keys.ToArray(), scene2.TimeOfDay.Keys.ToArray());

        // 新しいプロジェクトに戻すと、時刻も初めに戻る
        scene.ResetToDefault();
        Assert.AreEqual(TimeOfDayNode.DefaultHour, time.Hour);
        Assert.AreEqual(0, time.Keys.Count);
        Assert.IsTrue(time.FollowsSunPath);
    }

    [TestMethod]
    public void OldProjectKeepsTheWindowDirection()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var settings = new RenderSettings();
        scene.MainLight.Rotation = new Vector3(12f, 120f, 0f); // 前の版で「夕」を押し、窓に合わせた向き
        settings.Effects = settings.Effects with
        {
            ColorEnabled = true,
            Exposure = -0.15f,
            Contrast = 1.05f,
            Saturation = 1.15f,
            Temperature = 0.45f,
            BloomEnabled = true,
        };
        TimeOfDayPresets.UpgradeFromOldProject(scene, settings);
        Assert.AreEqual(120f, scene.TimeOfDay.NoonAzimuth, "今の向きを昼の向きにする");
        Assert.AreEqual(17f, scene.TimeOfDay.Hour, "前の版の「夕」の色合いは、時刻へ移す");
        Assert.IsFalse(settings.Effects.ColorEnabled, "色調補正は使わない状態に戻す（同じ色合いが時刻から重なる）");
        Assert.AreEqual(0f, settings.Effects.Exposure);
        Assert.IsTrue(settings.Effects.BloomEnabled);
        Assert.AreEqual(new Vector3(12f, 120f, 0f), scene.MainLight.Rotation, "サンライトは開いたまま");

        // 自分で決めた色調補正はそのまま
        SceneDocument scene2 = SceneDocument.CreateDefault();
        var custom = new RenderSettings();
        custom.Effects = custom.Effects with { ColorEnabled = true, Exposure = 0.3f };
        TimeOfDayPresets.UpgradeFromOldProject(scene2, custom);
        Assert.AreEqual(12f, scene2.TimeOfDay.Hour);
        Assert.AreEqual(0.3f, custom.Effects.Exposure);
        Assert.AreEqual(SceneLighting.MmdRotation.Y, scene2.TimeOfDay.NoonAzimuth);
    }

    [TestMethod]
    public void PaletteColorsRoundTripThroughHsv()
    {
        Assert.HasCount(3 * ColorPalette.SwatchesPerRow, ColorPalette.Swatches);
        foreach (ColorPalette.Swatch swatch in ColorPalette.Swatches)
        {
            Assert.IsTrue(ImageExport.TryParseColor(swatch.Color, out Vector3 color), swatch.Name);
            var (h, s, v) = ColorPalette.ToHsv(color);
            Assert.AreEqual(swatch.Color, ImageExport.FormatColor(ColorPalette.FromHsv(h, s, v)), $"{swatch.Name} は色合い・鮮やかさ・明るさから同じ色に戻る");
        }
        Assert.AreEqual((0f, 1f, 1f), ColorPalette.ToHsv(new Vector3(1f, 0f, 0f)));
        Assert.AreEqual(120f, ColorPalette.ToHsv(new Vector3(0f, 1f, 0f)).Hue, 1e-4f);
        Assert.AreEqual(240f, ColorPalette.ToHsv(new Vector3(0f, 0f, 1f)).Hue, 1e-4f);
        Assert.AreEqual((0f, 0f, 0.5f), ColorPalette.ToHsv(new Vector3(0.5f)), "灰色は色合い 0・鮮やかさ 0");
        Assert.AreEqual("#00FFFF", ImageExport.FormatColor(ColorPalette.FromHsv(180f, 1f, 1f)));
        Assert.AreEqual("#FF0000", ImageExport.FormatColor(ColorPalette.FromHsv(360f, 1f, 1f)), "360 度は 0 度と同じ");
        Assert.AreEqual("#FF0000", ImageExport.FormatColor(ColorPalette.FromHsv(-360f, 2f, 5f)), "範囲の外は収める");
        Assert.AreEqual("#000000", ImageExport.FormatColor(ColorPalette.FromHsv(float.NaN, float.NaN, float.NaN)));
    }

    [TestMethod]
    public void DraggingRightRaisesAndLeftLowers()
    {
        double px = NumberDrag.PixelsPerStep;
        Assert.AreEqual(1.0, NumberDrag.ValueAt(1.0, px - 0.5, 0.1), "1 刻みに足りなければ変えない");
        Assert.AreEqual(1.1, NumberDrag.ValueAt(1.0, px, 0.1), "右へ動かすと増える");
        Assert.AreEqual(0.7, NumberDrag.ValueAt(1.0, -3 * px, 0.1), "左へ動かすと減る");
        Assert.AreEqual(-2.0, NumberDrag.ValueAt(1.0, -30 * px, 0.1), "0 より下にもなる（範囲は受け取る側で収める）");
        Assert.AreEqual(1.01, NumberDrag.ValueAt(1.0, px, 0.1, fine: true), "Shift で 1/10");
        Assert.AreEqual(2.0, NumberDrag.ValueAt(1.0, px, 0.1, coarse: true), "Ctrl で 10 倍");
        Assert.AreEqual(55.0, NumberDrag.ValueAt(45.0, 10 * px, 1), "角度などの 1 刻み");
        Assert.AreEqual(0.3, NumberDrag.ValueAt(0.0, 3 * px, 0.1), "小数の誤差は丸める");
        Assert.AreEqual(5.0, NumberDrag.ValueAt(5.0, 100, 0), "刻みが 0 なら変えない");
        Assert.AreEqual(5.0, NumberDrag.ValueAt(5.0, double.NaN, 1));
    }
}
