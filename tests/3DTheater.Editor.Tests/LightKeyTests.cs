using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class LightKeyTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, LightNode Light) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        return (scene, playback, animation, scene.MainLight);
    }

    private static LightPose Pose(Vector3 rotation, Vector3 color, float intensity = 1f, Vector3 position = default, float range = 30f,
        float spotAngle = 40f, float spotSoftness = 0.3f) => new(position, rotation, color, intensity, range, spotAngle, spotSoftness);

    private static LightKey Key(int frame, LightPose pose) => new(frame, pose, BezierCurve.Linear);

    [TestMethod]
    public void LightKeysInterpolateEveryValue()
    {
        var track = new KeyTrack<LightKey>();
        track.Set(Key(0, Pose(new Vector3(30, 0, 0), new Vector3(1, 0, 0), 1f, new Vector3(0, 10, 0), 10f, 20f, 0f)));
        track.Set(Key(20, Pose(new Vector3(50, 360, 0), new Vector3(0, 0, 1), 3f, new Vector3(4, 10, -8), 30f, 60f, 1f)));

        Assert.IsTrue(KeyframeEvaluator.TryEvaluate(track, 10, out LightPose half));

        Assert.AreEqual(new Vector3(40, 180, 0), half.Rotation, "角度は成分ごとに直線で動く（1 周も表せる）");
        Assert.AreEqual(new Vector3(0.5f, 0, 0.5f), half.Color);
        Assert.AreEqual(2f, half.Intensity);
        Assert.AreEqual(new Vector3(2, 10, -4), half.Position);
        Assert.AreEqual(20f, half.Range);
        Assert.AreEqual(40f, half.SpotAngle);
        Assert.AreEqual(0.5f, half.SpotSoftness);
        KeyframeEvaluator.TryEvaluate(track, 30, out LightPose after);
        Assert.AreEqual(3f, after.Intensity, "最後のキーの後は、最後のキーの値");

        // 段階: 次のキーまで前のキーの値。なめらか: 動き出しがゆっくり
        track.Set(track.Keys[1] with { Kind = KeyInterpolationKind.Step });
        KeyframeEvaluator.TryEvaluate(track, 19, out LightPose step);
        Assert.AreEqual(1f, step.Intensity);
        track.Set(track.Keys[1] with { Kind = KeyInterpolationKind.Curve, Curve = KeyEditing.SmoothCurve });
        KeyframeEvaluator.TryEvaluate(track, 5, out LightPose eased);
        Assert.IsLessThan(1.5f, eased.Intensity, "なめらかな曲線では、始めはゆっくり変わる");
    }

    [TestMethod]
    public void LightPoseRoundTripsThroughTheNode()
    {
        var light = new LightNode("スポット", LightType.Spot);
        LightPose pose = Pose(new Vector3(10, -30, 5), new Vector3(0.2f, 0.4f, 0.6f), 2f, new Vector3(1, 12, 3), 50f, 25f, 0.1f);

        light.ApplyPose(pose);

        Assert.AreEqual(pose, light.Pose);
    }

    [TestMethod]
    public void KeyedLightEditsAreKeptUntilRegistered()
    {
        var (_, playback, animation, light) = Create();

        light.Color = new Vector3(1, 0, 0);
        Assert.IsFalse(light.HasUnregisteredChange, "キーの無い照明を変えても未登録にしない");

        light.Keys.Set(Key(0, Pose(new Vector3(30, 0, 0), Vector3.One)));
        light.Keys.Set(Key(30, Pose(new Vector3(60, 90, 0), new Vector3(0, 0, 1))));
        playback.Seek(15);
        Assert.AreEqual(new Vector3(45, 45, 0), light.Rotation, "再生位置のキーの値を当てはめる");
        Assert.AreEqual(new Vector3(0.5f, 0.5f, 1f), light.Color);
        Assert.IsFalse(light.HasUnregisteredChange, "当てはめは、手で変えたことにしない");

        light.Intensity = 2f;
        playback.Seek(20);
        Assert.IsTrue(light.HasUnregisteredChange);
        Assert.AreEqual(2f, light.Intensity, "キーのある照明の手の変更は、フレームを動かしても保つ");
        Assert.AreEqual(1, animation.UnregisteredCount);
        CollectionAssert.Contains(animation.Unregistered.ToList(), light);

        animation.DiscardUnregistered();
        Assert.AreEqual(1f, light.Intensity, "捨てると、キーの値に戻る");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(50, 60, 0), light.Rotation), 1e-4f);

        light.SpotSoftness = 0.9f;
        Assert.IsTrue(light.HasUnregisteredChange, "届く距離・広がり・ぼかしも、キーで動かす値");
        Assert.AreEqual(1, animation.RegisterUnregistered());
        Assert.IsTrue(light.Keys.TryGet(20, out LightKey key));
        Assert.AreEqual(0.9f, key.Pose.SpotSoftness);
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(50, 60, 0), key.Pose.Rotation), 1e-4f);
        Assert.IsFalse(light.HasUnregisteredChange);

        light.CastsShadow = false;
        light.IsVisible = false;
        Assert.IsFalse(light.HasUnregisteredChange, "影を落とすか・表示は、キーで動かさない");
    }

    [TestMethod]
    public void AutoKeyAndRegistrationCoverLights()
    {
        var (scene, playback, animation, light) = Create();
        animation.AutoKey = true;
        playback.Seek(5);

        light.Rotation = new Vector3(20, 10, 0);

        Assert.AreEqual(5, light.Keys.Keys.Single().Frame, "自動キーは、キーの無い照明にもキーを置く");
        Assert.AreEqual(new Vector3(20, 10, 0), light.Keys.Keys[0].Pose.Rotation);

        animation.AutoKey = false;
        LightNode added = LightPresenceAction.Create(scene, LightType.Point, Vector3.Zero);
        LightPresenceAction.Add(scene, new UndoService(), added);
        playback.Seek(12);
        CollectionAssert.AreEqual(new SceneNode[] { added, light }, animation.RegistrationTargets([added, light]).ToArray(),
            "選んだ照明は、登録の対象");
        Assert.AreEqual(2, animation.Register([added, light]));
        Assert.IsTrue(added.Keys.HasKeyAt(12));
        Assert.IsTrue(light.Keys.HasKeyAt(12));
        added.IsLocked = true;
        Assert.AreEqual(0, animation.Register([added]), "ロックした照明には置かない");
    }

    [TestMethod]
    public void LightKeyRegistrationCanBeUndone()
    {
        var (_, playback, animation, light) = Create();
        var undo = new UndoService();
        var edits = new EditService(undo, animation, playback);
        light.Keys.Set(Key(0, Pose(new Vector3(30, 0, 0), Vector3.One)));
        playback.Seek(10);
        light.Color = new Vector3(0, 1, 0);

        edits.Change("キーの登録", [light], () => animation.Register([light]), includeKeys: true);
        Assert.AreEqual(2, light.Keys.Count);
        Assert.IsFalse(light.HasUnregisteredChange);

        undo.Undo();
        Assert.AreEqual(1, light.Keys.Count, "元に戻すと、置いたキーが消える");
        Assert.IsTrue(light.HasUnregisteredChange, "未登録の印も戻る");
        Assert.AreEqual(new Vector3(0, 1, 0), light.Color);
        undo.Redo();
        Assert.AreEqual(2, light.Keys.Count);
    }

    [TestMethod]
    public void LightKeysCanBeMovedCopiedScaledAndCurved()
    {
        var (scene, _, _, light) = Create();
        light.Keys.SetRange([Key(0, Pose(Vector3.Zero, Vector3.One)), Key(10, Pose(new Vector3(0, 90, 0), Vector3.One)),
            Key(11, Pose(new Vector3(0, 180, 0), new Vector3(1, 0, 0)))]);

        Assert.IsTrue(KeyEditing.HasKeys(light));
        Assert.AreEqual(3, KeyEditing.CountKeys(light));
        Assert.HasCount(3, KeyEditing.InRange([scene.LightsGroup], 0, 20), "照明のまとまりで、中の照明のキーを選べる");

        KeyEditing.Move([new KeyRef(light, 10)], 5, duplicate: false);
        CollectionAssert.AreEqual(new[] { 0, 11, 15 }, light.Keys.Keys.Select(k => k.Frame).ToArray());
        KeyClipboard copied = KeyEditing.Copy([new KeyRef(light, 15)]);
        KeyEditing.Paste(copied, 30);
        Assert.AreEqual(new Vector3(0, 90, 0), light.Keys.Keys[^1].Pose.Rotation);
        Assert.AreEqual(1, KeyEditing.Delete([new KeyRef(light, 30)]));

        KeyEditing.SetInterpolation([new KeyRef(light, 15)], InterpolationPreset.Smooth);
        Assert.IsTrue(CurveEditing.TryGetCurve(new KeyRef(light, 15), 0, out BezierCurve curve, out _));
        Assert.AreEqual(KeyEditing.SmoothCurve, curve);
        CollectionAssert.AreEqual(new[] { "すべての値" }, CurveEditing.ChannelNames(light).ToArray());
        Assert.AreEqual(1, CurveEditing.SetCurve([new KeyRef(light, 15)], 0, BezierCurve.Linear));
        Assert.AreEqual(11, CurveEditing.PreviousKeyFrame(new KeyRef(light, 15)));
        string[] series = [.. CurveEditing.Sample(light, 0, 15).Select(s => s.Name)];
        CollectionAssert.Contains(series, "明るさ");
        CollectionAssert.DoesNotContain(series, "位置 X", "MMD の照明（平行光）は位置を描かない");

        // フレームの速さを 2 倍にすると、隣り合うキー（照明の切り替え）は段階にする
        light.Keys.ReplaceAll([Key(0, Pose(Vector3.Zero, Vector3.One)), Key(10, Pose(Vector3.Zero, Vector3.One)), Key(11, Pose(Vector3.Zero, Vector3.Zero))]);
        KeyEditing.ScaleTime([light], 2);
        CollectionAssert.AreEqual(new[] { 0, 20, 22 }, light.Keys.Keys.Select(k => k.Frame).ToArray());
        Assert.AreEqual(KeyInterpolationKind.Step, light.Keys.Keys[2].Kind);
    }

    [TestMethod]
    public void LightKeysAreSavedInTheProject()
    {
        var (scene, playback, animation, light) = Create();
        light.Keys.SetRange([Key(0, Pose(new Vector3(30, -45, 0), new Vector3(1, 0.5f, 0))),
            Key(20, Pose(new Vector3(60, 45, 0), Vector3.One, 1.5f)) with { Curve = KeyEditing.SmoothCurve, Kind = KeyInterpolationKind.Step }]);
        LightNode spot = LightPresenceAction.Create(scene, LightType.Spot, new Vector3(0, 10, 0));
        LightPresenceAction.Add(scene, new UndoService(), spot);
        spot.Keys.Set(Key(5, Pose(new Vector3(10, 20, 0), new Vector3(0, 1, 1), 2f, new Vector3(3, 8, -4), 40f, 30f, 0.2f)));
        spot.HasUnregisteredChange = true;
        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;

        var (scene2, playback2, animation2, light2) = Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, project, new LoadReport());

        CollectionAssert.AreEqual(light.Keys.Keys.ToArray(), light2.Keys.Keys.ToArray(), "MMD の照明のキー（曲線・段階も）が戻る");
        Assert.IsFalse(light2.HasUnregisteredChange);
        LightNode spot2 = scene2.AddedLights.Single();
        CollectionAssert.AreEqual(spot.Keys.Keys.ToArray(), spot2.Keys.Keys.ToArray(), "足した照明のキーも戻る");
        Assert.IsTrue(spot2.HasUnregisteredChange, "未登録の印も戻る");
    }

    [TestMethod]
    public void VmdLightDirectionsAndColorsBecomeLightKeys()
    {
        // MMD の初期の照明の向き (-0.5, -1, 0.5) は、シーンの MMD の照明の初期の回転と同じ
        Vector3 rotation = MmdLight.RotationOf(new Vector3(-0.5f, -1f, 0.5f))!.Value;
        Assert.AreEqual(0f, Vector3.Distance(SceneLighting.MmdRotation, rotation), 1e-3f);
        Assert.AreEqual(0f, Vector3.Distance(SceneLighting.MmdDirection, SceneLighting.DirectionOf(rotation)), 1e-5f);
        Assert.IsNull(MmdLight.RotationOf(Vector3.Zero), "向きの無いキーは使えない");
        Assert.AreEqual(180f + (MathF.Atan(0.1f) * 180f / MathF.PI), MmdLight.RotationOf(new Vector3(-0.1f, 0f, -1f), 170f)!.Value.Y, 1e-2f,
            "前のキーから逆回りしないようにつなぐ（-174° ではなく 186°）");

        var (_, _, _, light) = Create();
        light.Range = 55f;
        var vmd = new VmdDocument
        {
            LightKeys =
            [
                new VmdLightKey(0, new Vector3(SceneLighting.MmdIntensity), new Vector3(-0.5f, -1f, 0.5f)),
                new VmdLightKey(10, new Vector3(1f, 0.5f, 0f), new Vector3(0.5f, -1f, 0.5f)),
                new VmdLightKey(11, new Vector3(0.2f, 0.2f, 0.2f), new Vector3(0f, -1f, 0f)), // 切り替え
                new VmdLightKey(20, new Vector3(0.5f), Vector3.Zero), // 向きが読めない
            ],
        };
        var report = new LoadReport();

        LightImportResult result = LightMotion.Apply(vmd, light, frameRate: 60, report);

        Assert.AreEqual(3, result.Keys);
        Assert.AreEqual(22, result.LastFrame, "60 fps の作品では 2 倍の位置");
        LightKey first = light.Keys.Keys[0], bright = light.Keys.Keys[1];
        float intensity = 1f / SceneLighting.MmdIntensity;
        Assert.AreEqual(intensity, first.Pose.Intensity, 1e-4f, "MMD の色が 154/255 より明るいキーがあれば、全部のキーを同じ明るさにする");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(SceneLighting.MmdIntensity), first.Pose.Color * (first.Pose.Intensity * SceneLighting.MmdIntensity)), 1e-4f,
            "色 × 明るさ × 154/255 が MMD の色になる");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(1f, 0.5f, 0f), bright.Pose.Color * (bright.Pose.Intensity * SceneLighting.MmdIntensity)), 1e-4f);
        Assert.AreEqual(0f, Vector3.Distance(Vector3.Normalize(new Vector3(0.5f, -1f, 0.5f)), SceneLighting.DirectionOf(bright.Pose.Rotation)), 1e-5f);
        Assert.AreEqual(55f, bright.Pose.Range, "VMD に無い値は、今の値のまま");
        Assert.AreEqual(KeyInterpolationKind.Step, light.Keys.Keys[2].Kind, "1 フレームで切り替わる照明は段階にする");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("向きの読めない")));
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("明るさを")));
    }

    [TestMethod]
    public void ExportedLightMotionImportsBackTheSame()
    {
        var (scene, _, _, light) = Create();
        CameraNode camera = scene.FindFirst<CameraNode>()!;
        camera.Keys.Set(new CameraKey(0, Vector3.Zero, Vector3.Zero, 45, 30, CameraInterpolation.Linear));
        light.Keys.SetRange([Key(0, Pose(SceneLighting.MmdRotation, Vector3.One)), Key(30, Pose(new Vector3(40, 120, 0), new Vector3(1, 0.5f, 0.25f), 1.2f)),
            Key(45, Pose(new Vector3(80, 120, 0), new Vector3(0.1f, 0.1f, 0.1f))) with { Kind = KeyInterpolationKind.Step }]);
        var report = new LoadReport();

        VmdDocument written = CameraMotion.CreateVmd(camera, 30, report, light);
        VmdDocument read = VmdReader.Read(VmdWriter.Write(written)).Document!;
        var (_, _, _, other) = Create();
        LightMotion.Apply(read, other, 30, new LoadReport());

        Assert.HasCount(1, read.CameraKeys, "カメラのキーも一緒に書く");
        Assert.HasCount(4, read.LightKeys, "段階の補間は、1 つ前のフレームにキーを足して表す");
        Assert.AreEqual(44, read.LightKeys[2].Frame);
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("段階の補間")));
        LightKey a = light.Keys.Keys[1];
        LightKey b = other.Keys.Keys.Single(k => k.Frame == 30);
        Assert.AreEqual(0f, Vector3.Distance(SceneLighting.DirectionOf(a.Pose.Rotation), SceneLighting.DirectionOf(b.Pose.Rotation)), 1e-5f, "向きが戻る");
        Assert.AreEqual(0f, Vector3.Distance(a.Pose.Color * a.Pose.Intensity, b.Pose.Color * b.Pose.Intensity), 1e-4f, "色 × 明るさが戻る");
        KeyframeEvaluator.TryEvaluate(other.Keys, 44, out LightPose cut);
        Assert.AreEqual(0f, Vector3.Distance(a.Pose.Color * a.Pose.Intensity, cut.Color * cut.Intensity), 1e-3f, "切り替えの直前までは前の色");

        // 照明のキーだけのモーションも、空のモーションにはならない
        var lightOnly = new LoadReport();
        camera.Keys.Clear();
        Assert.HasCount(4, CameraMotion.CreateVmd(camera, 30, lightOnly, light).LightKeys);
        Assert.IsFalse(lightOnly.Issues.Any(i => i.Message.Contains("空のモーション")));
    }
}
