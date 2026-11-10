using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class LightTests
{
    [TestMethod]
    public void LightValuesAreKeptInRangeAndUndoable()
    {
        var light = new LightNode("点光源", LightType.Point)
        {
            Range = 0f,
            SpotAngle = 500f,
            SpotSoftness = -1f,
            Color = new Vector3(2f, 0.5f, -1f),
            Intensity = -3f,
        };
        Assert.AreEqual(LightNode.MinRange, light.Range);
        Assert.AreEqual(179f, light.SpotAngle);
        Assert.AreEqual(0f, light.SpotSoftness);
        Assert.AreEqual(new Vector3(1f, 0.5f, 0f), light.Color);
        Assert.AreEqual(0f, light.Intensity);
        light.Range = float.NaN;
        Assert.AreEqual(LightNode.MinRange, light.Range, "数でない値は入れない");

        NodeState before = light.CaptureState();
        light.Color = new Vector3(0f, 0f, 1f);
        light.Range = 12f;
        light.RestoreState(before);
        Assert.AreEqual(new Vector3(1f, 0.5f, 0f), light.Color, "元に戻すと色も戻る");
        Assert.AreEqual(LightNode.MinRange, light.Range);
    }

    [TestMethod]
    public void AddedLightsCanBeRemovedAndRestored()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var undo = new UndoService();
        LightNode point = LightPresenceAction.Create(scene, LightType.Point, new Vector3(0f, 10f, 0f));
        Assert.AreEqual("点光源", point.Name);
        LightPresenceAction.Add(scene, undo, point);
        LightNode second = LightPresenceAction.Create(scene, LightType.Point, Vector3.Zero);
        Assert.AreEqual("点光源 2", second.Name, "同じ種類は番号を付ける");
        LightPresenceAction.Add(scene, undo, second);
        CollectionAssert.AreEqual(new[] { point, second }, scene.AddedLights.ToArray());

        Assert.IsFalse(LightPresenceAction.Remove(scene, undo, scene.MainLight), "MMD の照明は外せない");
        Assert.IsTrue(LightPresenceAction.Remove(scene, undo, point));
        CollectionAssert.AreEqual(new[] { second }, scene.AddedLights.ToArray());
        undo.Undo();
        CollectionAssert.AreEqual(new[] { point, second }, scene.AddedLights.ToArray(), "元の並びに戻る");
        undo.Undo();
        undo.Undo();
        Assert.IsFalse(scene.AddedLights.Any(), "足したことも元に戻せる");
        undo.Redo();
        Assert.AreSame(point, scene.AddedLights.Single());

        // スポットライトは目標のほうを向く
        LightNode spot = LightPresenceAction.Create(scene, LightType.Spot, new Vector3(0f, 10f, 0f));
        Vector3 toTarget = Vector3.Normalize(new Vector3(0f, 10f, 0f) - spot.Position);
        Assert.IsGreaterThan(0.999f, Vector3.Dot(SceneLighting.DirectionOf(spot.Rotation), toTarget));

        scene.ResetToDefault();
        Assert.IsFalse(scene.AddedLights.Any(), "新しいプロジェクトでは足した照明を外す");
    }

    [TestMethod]
    public void OnlyVisibleLightsAreDrawnWithMmdStrength()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var undo = new UndoService();
        LightNode red = LightPresenceAction.Create(scene, LightType.Point, Vector3.Zero);
        red.Color = new Vector3(1f, 0f, 0f);
        red.Intensity = 2f;
        LightPresenceAction.Add(scene, undo, red);
        LightNode hidden = LightPresenceAction.Create(scene, LightType.Spot, Vector3.Zero);
        hidden.IsVisible = false;
        LightPresenceAction.Add(scene, undo, hidden);
        LightNode off = LightPresenceAction.Create(scene, LightType.Directional, Vector3.Zero);
        off.Intensity = 0f;
        LightPresenceAction.Add(scene, undo, off);

        IReadOnlyList<SceneLighting.AddedLight> drawn = SceneLighting.AddedLights(scene);
        Assert.HasCount(1, drawn, "隠した照明と明るさ 0 の照明は描かない");
        Assert.AreEqual(new Vector3(2f * SceneLighting.MmdIntensity, 0f, 0f), drawn[0].Color, "色 × 明るさ × 154/255");
        Assert.AreEqual(red.Position, drawn[0].Position);

        scene.MainLight.Color = new Vector3(1f, 0.5f, 0f);
        Assert.AreEqual(new Vector3(SceneLighting.MmdIntensity, SceneLighting.MmdIntensity * 0.5f, 0f), SceneLighting.From(scene.MainLight).Color,
            "MMD の照明にも色が付く");
    }

    [TestMethod]
    public void LightsAreSavedAndLoaded()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var undo = new UndoService();
        LightNode spot = LightPresenceAction.Create(scene, LightType.Spot, new Vector3(0f, 10f, 0f));
        spot.Color = new Vector3(0f, 1f, 1f);
        spot.SpotAngle = 25f;
        spot.IsVisible = false;
        LightPresenceAction.Add(scene, undo, spot);
        scene.MainLight.Color = new Vector3(1f, 0.8f, 0.6f);
        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;
        Assert.HasCount(1, project.Lights!);
        Assert.AreEqual("spot", project.Lights![0].Type);

        var (scene2, playback2, animation2, _) = AnimationFixtures.Create();
        LightPresenceAction.Add(scene2, undo, LightPresenceAction.Create(scene2, LightType.Point, Vector3.Zero)); // 開く前の照明は外す
        var report = new LoadReport();
        ProjectApply.ApplyScene(scene2, playback2, animation2, project, report);
        LightNode restored = scene2.AddedLights.Single();
        Assert.AreEqual(LightType.Spot, restored.Type);
        Assert.AreEqual(spot.Name, restored.Name);
        Assert.AreEqual(spot.Position, restored.Position);
        Assert.AreEqual(spot.Rotation, restored.Rotation);
        Assert.AreEqual(new Vector3(0f, 1f, 1f), restored.Color);
        Assert.AreEqual(25f, restored.SpotAngle);
        Assert.IsFalse(restored.IsVisible);
        Assert.AreEqual(ImageExport.FormatColor(new Vector3(1f, 0.8f, 0.6f)), ImageExport.FormatColor(scene2.MainLight.Color), "MMD の照明の色も戻る");

        // 種類の分からない照明は知らせて飛ばす。照明の無い前の版のファイルは、足した照明なし
        project.Lights.Add(new LightEntry { Name = "謎の照明", Type = "laser" });
        var report3 = new LoadReport();
        ProjectApply.ApplyScene(scene2, playback2, animation2, project, report3);
        Assert.AreEqual(1, scene2.AddedLights.Count());
        Assert.IsTrue(report3.Issues.Any(i => i.Message.Contains("謎の照明")));
        ProjectFile old = ProjectSerializer.Deserialize("""{"format":"3DTheater.Project","version":1,"nodes":{"light":{"intensity":1}}}""",
            new LoadReport())!;
        ProjectApply.ApplyScene(scene2, playback2, animation2, old, new LoadReport());
        Assert.IsFalse(scene2.AddedLights.Any());
        Assert.AreEqual(Vector3.One, scene2.MainLight.Color, "色の無い前の版は白");
    }
}
