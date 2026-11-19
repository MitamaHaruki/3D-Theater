using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

/// <summary>キー操作（ライブモード）: キーの組み合わせ・一覧・なめらかな出し入れ・表情を重ねる計算・保存。</summary>
[TestClass]
public sealed class HotkeyTests
{
    [TestMethod]
    public void GesturesAreWrittenAndReadBack()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x31);
        Assert.AreEqual("Ctrl+Shift+1", gesture.ToString());
        Assert.AreEqual(gesture, HotkeyGesture.Parse("Ctrl+Shift+1"));
        Assert.AreEqual(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x65), HotkeyGesture.Parse(" ctrl + ALT + テンキー5 "));
        Assert.AreEqual(new HotkeyGesture(HotkeyModifiers.None, 0x7C), HotkeyGesture.Parse("F13"));
        Assert.AreEqual("Win+Space", new HotkeyGesture(HotkeyModifiers.Windows, 0x20).ToString());

        Assert.IsNull(HotkeyGesture.Parse("Ctrl+"), "キーが無い");
        Assert.IsNull(HotkeyGesture.Parse("Hyper+A"), "知らない組み合わせのキー");
        Assert.IsNull(HotkeyGesture.Parse("Ctrl+Escape"), "使えないキー");
        Assert.IsNull(HotkeyGesture.Parse(null));

        Assert.IsTrue(gesture.HasModifier);
        Assert.IsFalse(new HotkeyGesture(HotkeyModifiers.None, 0x41).HasModifier);
        Assert.IsTrue(new HotkeyGesture(HotkeyModifiers.None, 0x41).IsValid);
        Assert.IsFalse(new HotkeyGesture(HotkeyModifiers.Control, 0x1B).IsValid, "Esc は使わない（キーを決めるのをやめるのに使う）");
    }

    [TestMethod]
    public void BindingsCanBeAddedChangedAndRemoved()
    {
        var settings = new HotkeySettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        HotkeyBinding smile = HotkeyBinding.Create() with { Gesture = HotkeyGesture.Parse("Ctrl+1"), Name = "笑顔" };
        HotkeyBinding hat = HotkeyBinding.Create(HotkeyAction.ModelVisibility) with { Gesture = HotkeyGesture.Parse("Ctrl+1"), Mode = HotkeyMode.Hold };
        Assert.IsTrue(settings.Add(smile));
        Assert.IsTrue(settings.Add(hat));
        Assert.IsFalse(settings.Add(hat), "同じものは足さない");
        Assert.IsFalse(settings.IsDuplicate(smile), "先にあるほうが効く");
        Assert.IsTrue(settings.IsDuplicate(hat), "同じキーは後のほうが効かない");

        settings.Replace(hat with { Gesture = HotkeyGesture.Parse("Ctrl+2") });
        Assert.IsFalse(settings.IsDuplicate(settings.Find(hat.Id)!));
        settings.Replace(settings.Find(hat.Id)!); // 同じなら知らせない
        settings.Remove(smile.Id);
        Assert.HasCount(1, settings.Bindings);
        Assert.AreEqual(4, changes);

        Assert.AreEqual(HotkeyMode.Hold, settings.Bindings[0].EffectiveMode);
        Assert.AreEqual(HotkeyMode.Toggle, (HotkeyBinding.Create(HotkeyAction.TrackingToggle) with { Mode = HotkeyMode.Hold }).EffectiveMode,
            "トラッキングは押したときに 1 回動くだけ");
        Assert.IsTrue(HotkeyActions.UsesName(HotkeyAction.Morph));
        Assert.IsFalse(HotkeyActions.UsesModel(HotkeyAction.VirtualCameraToggle));

        for (int i = 0; i < HotkeySettings.MaxBindings + 5; i++)
        {
            settings.Add(HotkeyBinding.Create());
        }
        Assert.HasCount(HotkeySettings.MaxBindings, settings.Bindings);
    }

    [TestMethod]
    public void StatesFadeInAndOut()
    {
        var state = new HotkeyState();
        Guid id = Guid.NewGuid();
        state.Set(id, true);
        Assert.IsTrue(state.IsOn(id));
        Assert.AreEqual(0f, state.Strength(id));
        Assert.IsTrue(state.Update(HotkeyState.FadeSeconds / 2));
        Assert.AreEqual(0.5f, state.Strength(id), 1e-4f);
        state.Update(1);
        Assert.AreEqual(1f, state.Strength(id));
        Assert.IsFalse(state.Update(0.1), "入りきったら変わらない");

        state.Set(id, false);
        Assert.IsFalse(state.IsOn(id));
        state.Update(HotkeyState.FadeSeconds / 4);
        Assert.AreEqual(0.75f, state.Strength(id), 1e-4f, "なめらかに消える");
        state.Update(1);
        Assert.AreEqual(0f, state.Strength(id));
        Assert.IsEmpty(state.Visible, "消えきったら外す");

        Guid hat = Guid.NewGuid();
        state.Set(hat, true, instant: true);
        Assert.AreEqual(1f, state.Strength(hat), "小物の表示はすぐ変える");
    }

    [TestMethod]
    public void ExpressionsOverrideOnlyTheMorphsTheyUse()
    {
        IReadOnlyDictionary<string, float> smile = new Dictionary<string, float> { ["笑い"] = 1f, ["にやり"] = 0.6f, ["あ"] = 0f };
        IReadOnlyDictionary<string, float> angry = new Dictionary<string, float> { ["怒り"] = 1f, ["にやり"] = 0.2f };
        Dictionary<string, (float Value, float Strength)> composed = LiveExpressions.Compose([(smile, 0.5f), (angry, 1f)]);
        Assert.IsFalse(composed.ContainsKey("あ"), "プリセットで 0 のモーフは使わない（口はトラッキングのまま）");
        Assert.AreEqual((1f, 0.5f), composed["笑い"]);
        Assert.AreEqual((0.6f, 0.5f), composed["にやり"], "値 × 強さ が大きいほう（0.6 × 0.5 > 0.2 × 1）");
        Assert.AreEqual((1f, 1f), composed["怒り"]);
        Assert.IsEmpty(LiveExpressions.Compose([(smile, 0f)]), "消えきった表情は使わない");

        Assert.AreEqual(0.3f, LiveExpressions.Blend(0.2f, 1f, 0.125f), 1e-5f);
        Assert.AreEqual(1f, LiveExpressions.Blend(0.9f, 1f, 1f), "強さ 1 なら表情の値（トラッキングより優先）");

        bool IsEye(string name) => name is "笑い" or "まばたき";
        Assert.AreEqual(0.5f, LiveExpressions.EyeSuppression([(smile, 0.5f), (angry, 1f)], IsEye), "目を閉じる表情の強さの分だけ、まばたきを止める");
        Assert.AreEqual(0f, LiveExpressions.EyeSuppression([(angry, 1f)], IsEye));
    }

    [TestMethod]
    public void HotkeysAreSavedWithTheModels()
    {
        Guid character = Guid.NewGuid();
        var settings = new HotkeySettings();
        settings.Add(HotkeyBinding.Create() with { Gesture = HotkeyGesture.Parse("Ctrl+Shift+1"), Name = "笑顔", Model = character });
        settings.Add(HotkeyBinding.Create(HotkeyAction.ModelVisibility) with { Gesture = HotkeyGesture.Parse("Ctrl+Shift+2"), Mode = HotkeyMode.Hold, Model = character });
        settings.Add(HotkeyBinding.Create(HotkeyAction.VirtualCameraToggle));
        var project = new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(new LiveOutputSettings(), null, settings) };
        string json = ProjectSerializer.Serialize(project);
        StringAssert.Contains(json, "\"key\":\"Ctrl\\u002BShift\\u002B1\"", "記号は \\u の形で書かれる（読み戻しは下で確かめる）");
        StringAssert.Contains(json, "\"action\":\"visibility\"");
        StringAssert.Contains(json, "\"mode\":\"hold\"");
        StringAssert.Contains(json, "\"action\":\"virtualCamera\"");

        Guid reopened = Guid.NewGuid();
        var read = new HotkeySettings();
        LiveEntry live = ProjectSerializer.Deserialize(json, new LoadReport())!.Live!;
        live.Hotkeys!.Add(new HotkeyEntry { Action = "新しい版の何か", Key = "Ctrl+9" });
        live.ApplyTo(read, id => id == character.ToString("N") ? reopened : null);
        Assert.HasCount(3, read.Bindings, "知らない動かすものは飛ばす");
        Assert.AreEqual(HotkeyGesture.Parse("Ctrl+Shift+1"), read.Bindings[0].Gesture);
        Assert.AreEqual("笑顔", read.Bindings[0].Name);
        Assert.AreEqual(reopened, read.Bindings[0].Model, "読み込み直したモデルにつながる");
        Assert.AreEqual(HotkeyMode.Hold, read.Bindings[1].Mode);
        Assert.IsNull(read.Bindings[2].Gesture, "キーを決めていないものもそのまま");

        Assert.DoesNotContain("hotkeys", ProjectSerializer.Serialize(new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(new LiveOutputSettings()) }),
            "キー操作が無ければ書かない");
    }

    [TestMethod]
    public void TimeOfDayKeysPickTheTimeAndMoveForward()
    {
        // 次の時刻: 朝 → 昼 → 夕 → 夜 → 朝
        Assert.AreEqual(8f, TimeOfDayPresets.NextHour(7f));
        Assert.AreEqual(12f, TimeOfDayPresets.NextHour(8f), "ボタンの時刻ちょうどなら、その次");
        Assert.AreEqual(17f, TimeOfDayPresets.NextHour(12f));
        Assert.AreEqual(17f, TimeOfDayPresets.NextHour(16.9f));
        Assert.AreEqual(22f, TimeOfDayPresets.NextHour(17f));
        Assert.AreEqual(8f, TimeOfDayPresets.NextHour(22f), "夜の次は朝");
        Assert.AreEqual(8f, TimeOfDayPresets.NextHour(23.5f));
        Assert.AreEqual(17f, TimeOfDayPresets.NextHour(36f), "36 時は次の日の 12 時");

        // キーの決まりの時刻
        Assert.AreEqual(17f, TimeOfDayHotkeys.TargetHour("17", 3f));
        Assert.AreEqual(22f, TimeOfDayHotkeys.TargetHour(TimeOfDayHotkeys.Next, 17f));
        Assert.AreEqual(1f, TimeOfDayHotkeys.TargetHour("25", 3f), "25 時は 1 時");
        Assert.IsNull(TimeOfDayHotkeys.TargetHour("夕方", 3f));
        Assert.IsNull(TimeOfDayHotkeys.TargetHour("50", 3f));
        Assert.IsNull(TimeOfDayHotkeys.TargetHour(null, 3f));
        Assert.AreEqual("夕", TimeOfDayHotkeys.Label("17"), "朝・夕・夜は季節で時刻が変わるので、時刻を書かない");
        Assert.AreEqual("13:30", TimeOfDayHotkeys.Label("13.5"));

        // 朝・昼・夕・夜は、時刻の項目の季節のボタンの時刻へ（冬の夕は 16 時。設計判断記録 96 章）
        SunDay winter = SunDay.Of(12, TimeOfDayNode.DefaultLatitude);
        Assert.AreEqual(16f, TimeOfDayHotkeys.TargetHour("17", 3f, winter));
        Assert.AreEqual(12f, TimeOfDayHotkeys.TargetHour("12", 3f, winter));
        Assert.AreEqual(16f, TimeOfDayHotkeys.TargetHour(TimeOfDayHotkeys.Next, 12f, winter), "次の時刻も季節に合わせる");
        Assert.AreEqual(17.5f, TimeOfDayHotkeys.TargetHour("17.5", 3f, winter), "決めた時刻はそのまま");
        Assert.IsNull(TimeOfDayHotkeys.Label("夕方"));
        Assert.IsTrue(HotkeyActions.UsesName(HotkeyAction.TimeOfDay));
        Assert.IsTrue(HotkeyActions.UsesDuration(HotkeyAction.TimeOfDay));
        Assert.IsFalse(HotkeyActions.UsesModel(HotkeyAction.TimeOfDay));
        Assert.IsFalse(HotkeyActions.HasMode(HotkeyAction.TimeOfDay), "押したときに 1 回変えるだけ");
        Assert.IsFalse(HotkeyActions.UsesDuration(HotkeyAction.Expression));

        // 移り変わり: 先へ進み、真ん中で半分、着いたら止まる
        var transition = new TimeOfDayTransition();
        Assert.IsTrue(transition.Start(17f, 22f, 5));
        Assert.AreEqual(19.5f, transition.Update(2.5)!.Value, 1e-4f, "なめらかな曲線でも、真ん中では半分");
        Assert.AreEqual(19.5f, transition.Update(0)!.Value, 1e-4f, "時間を進めなければ同じ");
        Assert.AreEqual(22f, transition.Update(2.5));
        Assert.IsFalse(transition.IsRunning, "着いたら止まる");
        Assert.IsNull(transition.Update(1));
        Assert.IsTrue(transition.Start(22f, 8f, 10), "夜から朝へ");
        Assert.AreEqual(3f, transition.Update(5)!.Value, 1e-4f, "夜明けを通って先へ進む（22 時 + 5 時間 = 3 時）");
        Assert.IsTrue(transition.Start(17f, 12f, 4), "夕から昼へも先へ進む（19 時間）");
        Assert.AreEqual(2.5f, transition.Update(2)!.Value, 1e-4f, "17 時 + 9.5 時間 = 2.5 時");
        Assert.IsFalse(transition.Start(8f, 8f, 5), "同じ時刻なら移らない");
        Assert.IsFalse(transition.Start(8f, 12f, 0), "0 秒ならすぐ変える");

        // 時刻を変える（元に戻すの履歴に入れず、時刻のキーがあっても未登録の変更にしない）
        var (scene, _, animation, _) = AnimationFixtures.Create();
        TimeOfDayNode time = scene.TimeOfDay;
        time.Keys.Set(new TimeKey(0, 12f, BezierCurve.Linear));
        Assert.IsTrue(TimeOfDayPresets.SetLive(animation, 41f));
        Assert.AreEqual(17f, time.Hour, "0〜24 時に収める");
        Assert.AreEqual(SunClock.SunRotation(time), scene.MainLight.Rotation);
        Assert.AreEqual(SunClock.Evening.Color, scene.MainLight.Color);
        Assert.IsFalse(time.HasUnregisteredChange);
        Assert.IsFalse(scene.MainLight.HasUnregisteredChange);
        scene.MainLight.IsLocked = true;
        Assert.IsFalse(TimeOfDayPresets.SetLive(animation, 8f), "サンライトがロックされていれば変えない");
        Assert.AreEqual(17f, time.Hour);

        // 保存して開き直す（時刻と移り変わる秒数）
        var settings = new HotkeySettings();
        settings.Add(HotkeyBinding.Create(HotkeyAction.TimeOfDay) with { Gesture = HotkeyGesture.Parse("Ctrl+Alt+N"), Name = TimeOfDayHotkeys.Next, Duration = 5 });
        settings.Add(HotkeyBinding.Create(HotkeyAction.TimeOfDay) with { Gesture = HotkeyGesture.Parse("Ctrl+Alt+E"), Name = "17" });
        settings.Add(HotkeyBinding.Create() with { Name = "笑顔", Duration = 9 });
        string json = ProjectSerializer.Serialize(new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(new LiveOutputSettings(), null, settings) });
        StringAssert.Contains(json, "\"action\":\"timeOfDay\"");
        StringAssert.Contains(json, "\"duration\":5");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Count(json, "\"duration\""), "すぐ変えるもの・表情には書かない");
        var read = new HotkeySettings();
        ProjectSerializer.Deserialize(json, new LoadReport())!.Live!.ApplyTo(read, _ => null);
        Assert.AreEqual(HotkeyAction.TimeOfDay, read.Bindings[0].Action);
        Assert.AreEqual(TimeOfDayHotkeys.Next, read.Bindings[0].Name);
        Assert.AreEqual(5, read.Bindings[0].Duration);
        Assert.AreEqual("17", read.Bindings[1].Name);
        Assert.AreEqual(0, read.Bindings[1].Duration);
        Assert.AreEqual(0, read.Bindings[2].Duration, "表情は秒数を持たない");
    }

    [TestMethod]
    public void CameraSwitchKeysAreSavedWithTheCameraNumber()
    {
        // ライブカメラと、足したカメラ 2 つ（設計判断記録 78 章の 7 段目）
        SceneDocument scene = SceneDocument.CreateDefault();
        var undo = new UndoService();
        CameraNode second = CameraPresenceAction.Create(scene, scene.MainCamera);
        CameraPresenceAction.Add(scene, undo, second);
        CameraNode third = CameraPresenceAction.Create(scene, scene.MainCamera);
        CameraPresenceAction.Add(scene, undo, third);
        CameraNode gone = CameraPresenceAction.Create(scene, scene.MainCamera); // 外したカメラ（シーンに無い）

        var settings = new HotkeySettings();
        settings.Add(HotkeyBinding.Create(HotkeyAction.CameraSwitch) with { Gesture = HotkeyGesture.Parse("Ctrl+Alt+1") });
        settings.Add(HotkeyBinding.Create(HotkeyAction.CameraSwitch) with { Gesture = HotkeyGesture.Parse("Ctrl+Alt+3"), Model = third.Id });
        settings.Add(HotkeyBinding.Create(HotkeyAction.CameraSwitch) with { Model = gone.Id });
        Assert.IsFalse(HotkeyActions.HasMode(HotkeyAction.CameraSwitch), "押したときに 1 回切り替えるだけ");
        Assert.IsTrue(HotkeyActions.UsesCamera(HotkeyAction.CameraSwitch));
        Assert.IsFalse(HotkeyActions.UsesModel(HotkeyAction.CameraSwitch));

        string json = ProjectSerializer.Serialize(new ProjectFile
        {
            Mode = AppModes.LiveName,
            Live = LiveEntry.From(new LiveOutputSettings(), null, settings, null, new LiveCameraSettings(), scene),
        });
        StringAssert.Contains(json, "\"action\":\"camera\"");
        StringAssert.Contains(json, "\"camera\":2", "足したカメラの 2 番目");

        // 読み込み直すと、カメラの Id は新しくなる（番号でつなぐ）
        SceneDocument reopened = SceneDocument.CreateDefault();
        CameraNode reopenedSecond = CameraPresenceAction.Create(reopened, reopened.MainCamera);
        CameraPresenceAction.Add(reopened, new UndoService(), reopenedSecond);
        CameraNode reopenedThird = CameraPresenceAction.Create(reopened, reopened.MainCamera);
        CameraPresenceAction.Add(reopened, new UndoService(), reopenedThird);
        var read = new HotkeySettings();
        ProjectSerializer.Deserialize(json, new LoadReport())!.Live!.ApplyTo(read, _ => null, reopened);
        Assert.HasCount(3, read.Bindings);
        Assert.IsNull(read.Bindings[0].Model, "ライブカメラ");
        Assert.AreEqual(reopenedThird.Id, read.Bindings[1].Model, "番号で、読み込み直したカメラにつながる");
        Assert.AreEqual(Guid.Empty, read.Bindings[2].Model, "見つからなかったカメラは、どのカメラでもない（選び直してもらう）");

        // 足したカメラが無いファイルでは、番号のカメラも見つからない
        var missing = new HotkeySettings();
        ProjectSerializer.Deserialize(json, new LoadReport())!.Live!.ApplyTo(missing, _ => null, SceneDocument.CreateDefault());
        Assert.AreEqual(Guid.Empty, missing.Bindings[1].Model);
    }
}
