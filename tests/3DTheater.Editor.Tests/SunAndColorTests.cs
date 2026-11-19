using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

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

        // 保存して開き直しても同じ（時刻・昼の向き・向きを動かすか・季節・緯度・キー）
        time.NoonAzimuth = 30f;
        time.FollowsSunPath = false;
        time.Month = 7;
        time.Latitude = 43.1f;
        string json = ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, []));
        var (scene2, playback2, animation2, _) = AnimationFixtures.Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, ProjectSerializer.Deserialize(json, new LoadReport())!, new LoadReport());
        Assert.AreEqual(time.Hour, scene2.TimeOfDay.Hour);
        Assert.AreEqual(30f, scene2.TimeOfDay.NoonAzimuth);
        Assert.IsFalse(scene2.TimeOfDay.FollowsSunPath);
        Assert.AreEqual(7, scene2.TimeOfDay.Month);
        Assert.AreEqual(43.1f, scene2.TimeOfDay.Latitude);
        CollectionAssert.AreEqual(time.Keys.ToArray(), scene2.TimeOfDay.Keys.ToArray());

        // 季節と緯度の無い前の版のファイルは、春分と初めの緯度（今までと同じ道筋）
        var (scene3, playback3, animation3, _) = AnimationFixtures.Create();
        scene3.TimeOfDay.Month = 12;
        ProjectApply.ApplyScene(scene3, playback3, animation3, ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"nodes":{"timeOfDay":{"name":"時刻","visible":true,"hour":17}}}""", new LoadReport())!, new LoadReport());
        Assert.AreEqual(TimeOfDayNode.DefaultMonth, scene3.TimeOfDay.Month);
        Assert.AreEqual(TimeOfDayNode.DefaultLatitude, scene3.TimeOfDay.Latitude);

        // 新しいプロジェクトに戻すと、時刻も初めに戻る
        scene.ResetToDefault();
        Assert.AreEqual(TimeOfDayNode.DefaultHour, time.Hour);
        Assert.AreEqual(0, time.Keys.Count);
        Assert.IsTrue(time.FollowsSunPath);
        Assert.AreEqual(TimeOfDayNode.DefaultMonth, time.Month);
        Assert.AreEqual(TimeOfDayNode.DefaultLatitude, time.Latitude);
    }

    [TestMethod]
    public void SeasonAndLatitudeMoveTheSunPath()
    {
        // 初め（3 月・35.3°）は今までと同じ: 6 時に昇り 18 時に沈む。9 月も同じ
        Assert.AreEqual(6f, SunDay.Default.Sunrise);
        Assert.AreEqual(18f, SunDay.Default.Sunset);
        Assert.AreEqual(0f, SunDay.Default.Declination);
        Assert.AreEqual(SunDay.Default, SunDay.Of(9, TimeOfDayNode.DefaultLatitude));
        CollectionAssert.AreEqual(new[] { 8f, 12f, 17f, 22f },
            new[] { TimeOfDay.Morning, TimeOfDay.Noon, TimeOfDay.Evening, TimeOfDay.Night }.Select(TimeOfDayPresets.HourOf).ToArray());

        // 夏至: 昼の太陽が高く、朝早く昇って遅く沈む（12 時をはさんで同じだけ）。日の出の太陽は、真東より北から
        SunDay summer = SunDay.Of(6, TimeOfDayNode.DefaultLatitude);
        Assert.AreEqual(SunDay.Tilt, summer.Declination);
        Assert.AreEqual(90f - TimeOfDayNode.DefaultLatitude + SunDay.Tilt, SunClock.PositionAt(12f, summer).Elevation, 1e-3f, "夏の昼は 78.2°");
        Assert.AreEqual(4.81f, summer.Sunrise, 0.01f);
        Assert.AreEqual(19.19f, summer.Sunset, 0.01f);
        Assert.AreEqual(12f, (summer.Sunrise + summer.Sunset) / 2f, 1e-4f);
        Assert.IsLessThan(-90f, SunClock.PositionAt(summer.Sunrise + 0.01f, summer).Azimuth, "夏の朝日は北東から");
        Assert.IsFalse(SunClock.PositionAt(summer.Sunrise + 0.01f, summer).IsMoon);
        Assert.IsTrue(SunClock.PositionAt(summer.Sunrise - 0.01f, summer).IsMoon, "日の出の前は月");
        Assert.AreEqual(90f - TimeOfDayNode.DefaultLatitude - SunDay.Tilt, SunClock.PositionAt(0f, summer).Elevation, 1e-3f, "夏の満月は低い（31.3°）");

        // 冬至: 昼の太陽が低く、昼が短い。満月は高い
        SunDay winter = SunDay.Of(12, TimeOfDayNode.DefaultLatitude);
        Assert.AreEqual(90f - TimeOfDayNode.DefaultLatitude - SunDay.Tilt, SunClock.PositionAt(12f, winter).Elevation, 1e-3f, "冬の昼は 31.3°");
        Assert.AreEqual(24f - summer.DayLength, winter.DayLength, 1e-3f, "冬の昼の長さは、夏の夜の長さ");
        Assert.AreEqual(90f - TimeOfDayNode.DefaultLatitude + SunDay.Tilt, SunClock.PositionAt(0f, winter).Elevation, 1e-3f, "冬の満月は高い");
        Assert.AreEqual(-SunClock.PositionAt(9f, winter).Azimuth, SunClock.PositionAt(15f, winter).Azimuth, 1e-3f, "昼をはさんで左右に同じだけ");

        // 光の値を決めた時刻も、日の出・日の入りに合わせて動く（決めた時刻ちょうどはその値）
        IReadOnlyList<(float Hour, DaylightLook Look)> anchors = SunClock.AnchorsFor(summer);
        Assert.AreEqual(summer.Sunrise, anchors[2].Hour);
        Assert.AreEqual(SunClock.Morning, SunClock.LookAt(anchors[3].Hour, summer));
        Assert.AreEqual(SunClock.Evening, SunClock.LookAt(anchors[6].Hour, summer));
        Assert.AreEqual(SunClock.Twilight, SunClock.LookAt(winter.Sunset, winter), "冬は 16:49 に日が沈む");
        Assert.AreEqual(SunClock.Night, SunClock.LookAt(19f, winter), "冬の 19 時はもう夜");
        Assert.AreNotEqual(SunClock.Night, SunClock.LookAt(19f, summer), "夏の 19 時はまだ明るい");
        for (int i = 1; i < anchors.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(anchors[i - 1].Hour, anchors[i].Hour, $"時刻の順（夏 {i}）");
        }
        SunDay farWinter = SunDay.Of(12, TimeOfDayNode.MaxLatitude);
        IReadOnlyList<(float Hour, DaylightLook Look)> far = SunClock.AnchorsFor(farWinter);
        for (int i = 1; i < far.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(far[i - 1].Hour, far[i].Hour, $"昼がとても短くても時刻の順（{i}）");
        }

        // ボタンの時刻（15 分刻み）: 夏は朝が早く夕が遅い。冬はその逆
        Assert.AreEqual(7.25f, TimeOfDayPresets.HourOf(TimeOfDay.Morning, summer));
        Assert.AreEqual(18f, TimeOfDayPresets.HourOf(TimeOfDay.Evening, summer));
        Assert.AreEqual(22.5f, TimeOfDayPresets.HourOf(TimeOfDay.Night, summer));
        Assert.AreEqual(8.75f, TimeOfDayPresets.HourOf(TimeOfDay.Morning, winter));
        Assert.AreEqual(16f, TimeOfDayPresets.HourOf(TimeOfDay.Evening, winter));
        Assert.AreEqual(21.5f, TimeOfDayPresets.HourOf(TimeOfDay.Night, winter));
        Assert.AreEqual(12f, TimeOfDayPresets.HourOf(TimeOfDay.Noon, winter));

        // 緯度の低い所の夏は、昼に頭の上より北を通る。昼の向きから見て、朝は左・夕は右のまま
        SunDay tropic = SunDay.Of(6, 10f);
        Assert.AreEqual(90f - (SunDay.Tilt - 10f), SunClock.PositionAt(12f, tropic).Elevation, 1e-3f);
        Assert.AreEqual(0f, SunClock.PositionAt(12f, tropic).Azimuth, 1e-3f);
        Assert.IsLessThan(0f, SunClock.PositionAt(9f, tropic).Azimuth, "朝は左");
        Assert.IsGreaterThan(0f, SunClock.PositionAt(15f, tropic).Azimuth, "夕は右");
        Assert.AreEqual(90f, SunClock.PositionAt(12f, SunDay.Of(3, 0f)).Elevation, 1e-3f, "赤道の春分の昼は真上");

        // 範囲: 緯度は 0〜60、季節は 1〜12
        var time = new TimeOfDayNode { Latitude = 80f, Month = 15 };
        Assert.AreEqual(TimeOfDayNode.MaxLatitude, time.Latitude);
        Assert.AreEqual(12, time.Month);
        time.Latitude = -5f;
        time.Month = 0;
        Assert.AreEqual(0f, time.Latitude);
        Assert.AreEqual(1, time.Month);
        time.Latitude = float.NaN;
        Assert.AreEqual(0f, time.Latitude, "数でない値は入れない");
        Assert.AreEqual("6 月（夏至）", TimeOfDayPresets.MonthLabel(6));
        Assert.AreEqual("7 月", TimeOfDayPresets.MonthLabel(7));
    }

    [TestMethod]
    public void TimeKeysAreExportedAsLightKeysAlongTheSunPath()
    {
        var (scene, _, _, _, _, _) = Create();
        TimeOfDayNode time = scene.TimeOfDay;
        LightNode sun = scene.MainLight;
        CameraNode camera = scene.FindFirst<CameraNode>()!;
        time.Month = 6; // 夏の道筋でも
        time.Keys.SetRange([
            new TimeKey(0, 8f, BezierCurve.Linear, KeyInterpolationKind.Curve),
            new TimeKey(300, 17f, BezierCurve.Linear, KeyInterpolationKind.Curve),
            new TimeKey(400, 22f, BezierCurve.Linear, KeyInterpolationKind.Step), // 夜へ切り替える
        ]);
        sun.Keys.Set(new LightKey(0, new LightPose(sun.Position, new Vector3(30, 0, 0), Vector3.One, 1f, sun.Range, sun.SpotAngle, sun.SpotSoftness), BezierCurve.Linear));
        var report = new LoadReport();

        VmdDocument written = VmdReader.Read(VmdWriter.Write(CameraMotion.CreateVmd(camera, 30, report, sun, scene.SelfShadow, time))).Document!;
        IReadOnlyList<VmdLightKey> keys = written.LightKeys;

        Assert.AreEqual(0, keys[0].Frame);
        Assert.AreEqual(400, keys[^1].Frame);
        Assert.IsLessThan(200, keys.Count, "1 フレームごとには書かない（直線でつないで違わない所は省く）");
        Assert.IsGreaterThan(5, keys.Count, "太陽が空の道筋をたどるように分ける");
        Assert.IsTrue(keys.Any(k => k.Frame == 399) && keys.Any(k => k.Frame == 400), "段階の切り替えの前後");

        // MMD と同じに直線でつなぐと、どのフレームでも時刻の光と同じ（許しの中）
        SunDay day = SunDay.Of(time);
        for (int frame = 0; frame <= 400; frame++)
        {
            Assert.IsTrue(KeyframeEvaluator.TryEvaluate(time.Keys, frame, out float hour));
            DaylightLook look = SunClock.LookAt(hour, day);
            Vector3 color = look.Color * (look.Intensity * SceneLighting.MmdIntensity);
            Vector3 direction = SceneLighting.DirectionOf(SunClock.SunRotationAt(time, hour));
            int i = Math.Max(0, keys.Count(k => k.Frame <= frame) - 1);
            VmdLightKey a = keys[i], b = keys[Math.Min(i + 1, keys.Count - 1)];
            float t = b.Frame > a.Frame ? (frame - a.Frame) / (float)(b.Frame - a.Frame) : 0f;
            Vector3 mmdColor = Vector3.Lerp(a.Color, b.Color, t);
            Vector3 mmdDirection = Vector3.Normalize(Vector3.Lerp(a.Direction, b.Direction, t));
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(mmdDirection, direction), -1f, 1f)) * 180f / MathF.PI;
            Assert.IsLessThan(LightMotion.TimeAngleTolerance + 0.01f, angle, $"{frame} フレーム目の向き");
            Vector3 diff = Vector3.Abs(mmdColor - color);
            Assert.IsLessThan(LightMotion.TimeColorTolerance + 1e-4f, MathF.Max(diff.X, MathF.Max(diff.Y, diff.Z)), $"{frame} フレーム目の色");
        }
        // （上のフレームごとの確かめに、夏の昼の高い所を通る 133 フレーム目も入っている）
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("時刻で動くサンライト")));
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("サンライトの照明のキー")), "サンライトの照明のキーは書かないと知らせる");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("色合い")));

        // 60 fps の作品は、MMD の 30 fps に直す
        var report60 = new LoadReport();
        IReadOnlyList<VmdLightKey> keys60 = LightMotion.CreateKeysFromTime(time, sun, 60, report60);
        Assert.AreEqual(200, keys60[^1].Frame);
        Assert.IsTrue(report60.Issues.Any(i => i.Message.Contains("30 fps")));

        // 時刻のキーが無ければ、今まで通りサンライトの照明のキーを書く
        time.Keys.Clear();
        Assert.HasCount(1, CameraMotion.CreateVmd(camera, 30, new LoadReport(), sun, scene.SelfShadow, time).LightKeys);
    }

    [TestMethod]
    public void SeasonChangesTheSunAtTheSameTime()
    {
        var (scene, _, animation, settings, undo, edits) = Create();
        LightNode sun = scene.MainLight;
        TimeOfDayNode time = scene.TimeOfDay;
        TimeOfDayPresets.SetHour(edits, animation, settings, 17f, "時刻");
        float equinox = sun.Rotation.X;

        // 季節を冬にすると、同じ 17 時でも日の入りの後（弱い光と夜の色合い）
        TimeOfDayPresets.SetSeason(edits, animation, 12, null, "季節");
        Assert.AreEqual(17f, time.Hour, "時刻は変えない");
        Assert.AreEqual(SunClock.SunRotation(time, sun.Rotation.Z), sun.Rotation);
        Assert.AreEqual(SunClock.LookAt(17f, SunDay.Of(time)).Color, sun.Color);
        Assert.AreNotEqual(SunClock.Evening.Grade, SunClock.GradeFor(settings.Effects, time), "描くときの色合いも季節で変わる");
        Assert.AreEqual("季節", undo.UndoDescription);
        undo.Undo();
        Assert.AreEqual(TimeOfDayNode.DefaultMonth, time.Month);
        Assert.AreEqual(equinox, sun.Rotation.X, 1e-4f);

        // 夕のボタンは、季節に合わせた時刻（夏は 18 時）
        TimeOfDayPresets.SetSeason(edits, animation, 6, null, "季節");
        TimeOfDayPresets.Apply(edits, animation, settings, TimeOfDay.Evening);
        Assert.AreEqual(18f, time.Hour);
        Assert.AreEqual(SunClock.LookAt(18f, SunDay.Of(time)), new DaylightLook(sun.Color, sun.Intensity, SunClock.LookAt(time).Grade));

        // 緯度を続けて変えると、1 回の元に戻すにまとまる
        int before = undo.UndoCount;
        foreach (float latitude in new[] { 36f, 37f, 38f })
        {
            TimeOfDayPresets.SetSeason(edits, animation, null, latitude, "緯度", mergeKey: "time:緯度");
        }
        Assert.AreEqual(before + 1, undo.UndoCount);
        Assert.AreEqual(38f, time.Latitude);

        // 時刻のキーで動かすときも、季節の道筋をたどる
        time.Keys.Set(new TimeKey(0, 12f, BezierCurve.Linear, KeyInterpolationKind.Curve));
        animation.ApplyFrame();
        Assert.AreEqual(90f - 38f + SunDay.Tilt, sun.Rotation.X, 1e-3f, "夏の昼の高さ");
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
