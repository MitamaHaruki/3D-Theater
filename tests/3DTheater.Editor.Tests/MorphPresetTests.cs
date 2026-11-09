using System.Text.Json;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class MorphPresetTests
{
    private static (ModelNode Model, AnimationService Animation, EditService Edits) Create()
    {
        var (_, playback, animation, model) = AnimationFixtures.Create();
        return (model, animation, new EditService(new UndoService(), animation, playback));
    }

    private static MorphPreset Preset(string name, params (string Morph, float Value)[] values) =>
        new(name, values.Select(v => KeyValuePair.Create(v.Morph, v.Value)));

    [TestMethod]
    public void CaptureKeepsZeroValuesAndMatchesMorphsByName()
    {
        var (model, _, _) = Create();
        model.Morphs[0].Weight = 0.5f;

        MorphPreset preset = MorphPreset.Capture("  笑顔  ", model.Morphs);

        Assert.AreEqual("笑顔", preset.Name, "前後の空白は除く");
        Assert.AreEqual(0.5f, preset.Values["まばたき"]);
        Assert.AreEqual(0f, preset.Values["あ"], "0 のモーフも覚える（当てると 0 に戻す）");
        Assert.AreEqual(2, preset.CountMatches(model));
        Assert.AreEqual(1, Preset("口だけ", ("あ", 1), ("い", 1)).CountMatches(model), "無い名前は数えない");
        Assert.ThrowsExactly<ArgumentException>(() => Preset(" "));
        Assert.AreEqual(1f, Preset("大きすぎ", ("あ", 3)).Values["あ"], "値は 0〜1 に収める");
    }

    [TestMethod]
    public void LibraryReplacesRenamesRemovesAndRoundTrips()
    {
        var library = new MorphPresetLibrary();
        int changes = 0;
        library.Changed += (_, _) => changes++;
        library.Put(Preset("笑顔", ("まばたき", 1)));
        library.Put(Preset("驚き", ("あ", 1)));
        library.Put(Preset("笑顔", ("まばたき", 0.5f))); // 同じ名前は同じ位置で置き換える

        CollectionAssert.AreEqual(new[] { "笑顔", "驚き" }, library.Presets.Select(p => p.Name).ToArray());
        Assert.AreEqual(0.5f, library.Find("笑顔")!.Values["まばたき"]);
        Assert.IsFalse(library.Rename("驚き", "笑顔"), "ほかと同じ名前にはしない");
        Assert.IsFalse(library.Rename("驚き", "  "));
        Assert.IsTrue(library.Rename("驚き", "びっくり"));
        Assert.AreEqual(1f, library.Find("びっくり")!.Values["あ"], "名前を変えても値はそのまま");
        Assert.AreEqual(4, changes);

        MorphPresetLibrary loaded = MorphPresetLibrary.FromJson(library.ToJson());
        CollectionAssert.AreEqual(new[] { "笑顔", "びっくり" }, loaded.Presets.Select(p => p.Name).ToArray());
        Assert.AreEqual(0.5f, loaded.Find("笑顔")!.Values["まばたき"]);

        Assert.IsTrue(library.Remove("笑顔"));
        Assert.IsFalse(library.Remove("笑顔"));
        Assert.HasCount(1, library.Presets);
    }

    [TestMethod]
    public void BrokenEntriesAreSkippedAndUnknownVersionsAreRejected()
    {
        const string json = """
            {
              "version": 1,
              "presets": [
                { "name": "", "values": { "あ": 1 } },
                { "name": "値なし" },
                { "name": "笑顔", "values": { "まばたき": 1 } },
                { "name": "笑顔", "values": { "まばたき": 0 } }
              ]
            }
            """;
        MorphPresetLibrary library = MorphPresetLibrary.FromJson(json);
        Assert.HasCount(1, library.Presets);
        Assert.AreEqual(1f, library.Presets[0].Values["まばたき"], "同じ名前は最初のもの");

        Assert.ThrowsExactly<JsonException>(() => MorphPresetLibrary.FromJson("""{ "version": 99, "presets": [] }"""));
    }

    [TestMethod]
    public void MixingMovesFromTheBaseTowardEachPresetAndIsOneUndo()
    {
        var (model, animation, edits) = Create();
        MorphNode blink = model.Morphs[0], a = model.Morphs[1];
        blink.Weight = 0.2f;
        MorphPreset smile = Preset("笑顔", ("まばたき", 1), ("あ", 0));
        MorphPreset surprise = Preset("驚き", ("あ", 1));
        using var mix = new MorphMixSession(model, edits);

        mix.SetRatio(smile, 0.5f);
        Assert.AreEqual(0.6f, blink.Weight, 1e-6f, "0.2 から 1 へ半分寄せる");
        Assert.AreEqual(0f, a.Weight);

        mix.SetRatio(surprise, 1f);
        Assert.AreEqual(0.6f, blink.Weight, 1e-6f, "驚きに無いモーフは笑顔の分だけ");
        Assert.AreEqual(1f, a.Weight, 1e-6f, "笑顔の 0（基準と同じ）と驚きの 1 を足す");
        Assert.IsTrue(mix.IsMixing);

        mix.SetRatio(smile, 0f);
        Assert.AreEqual(0.2f, blink.Weight, 1e-6f, "割合を 0 に戻すと基準の値に戻る");
        Assert.AreEqual(2, animation.UnregisteredCount, "混ぜた値も手で変えたのと同じく未登録の変更になる");

        Assert.AreEqual(1, edits.Undo.UndoCount, "続けて変えた分は 1 つの操作");
        int cleared = 0;
        mix.Cleared += (_, _) => cleared++;
        edits.Undo.Undo();
        Assert.AreEqual(0.2f, blink.Weight, 1e-6f);
        Assert.AreEqual(0f, a.Weight);
        Assert.AreEqual(1, cleared, "元に戻すと、割合を 0 にする");
        Assert.AreEqual(0f, mix.RatioOf("驚き"));
        Assert.IsFalse(mix.IsMixing);
    }

    [TestMethod]
    public void OtherEditsBecomeTheNewBase()
    {
        var (model, _, edits) = Create();
        MorphNode blink = model.Morphs[0];
        MorphPreset smile = Preset("笑顔", ("まばたき", 1));
        using var mix = new MorphMixSession(model, edits);
        mix.SetRatio(smile, 1f);

        edits.Change("まばたきの値", [blink], () => blink.Weight = 0.5f);
        Assert.AreEqual(0f, mix.RatioOf("笑顔"), "手で変えたら割合を 0 にする");

        mix.SetRatio(smile, 0.5f);
        Assert.AreEqual(0.75f, blink.Weight, 1e-6f, "手で変えた 0.5 から寄せる");
    }

    [TestMethod]
    public void LockedMorphsAreNotMixed()
    {
        var (model, _, edits) = Create();
        MorphNode blink = model.Morphs[0], a = model.Morphs[1];
        blink.IsLocked = true;
        using var mix = new MorphMixSession(model, edits);

        mix.SetRatio(Preset("笑顔", ("まばたき", 1), ("あ", 1)), 1f);

        Assert.AreEqual(0f, blink.Weight, "ロックしたモーフは変えない");
        Assert.AreEqual(1f, a.Weight);
    }
}
