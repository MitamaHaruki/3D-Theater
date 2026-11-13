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
}
