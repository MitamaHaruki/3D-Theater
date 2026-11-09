using System.Numerics;
using System.Text;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>
/// 元に戻す・やり直しの検証（指示書 Phase 3「Undo/Redo の検証」）。でたらめな編集（値・キー・補間・登録・破棄・ドラッグ・
/// フレームの速さ・表情の混合・鍵・自動キーの切り替え・再生位置の移動）を続けたあと、
/// 全部元に戻すと 1 つずつ前の状態に戻り、全部やり直すと 1 つずつ後の状態に進むことを、いくつもの乱数の種で確かめる。
/// 状態はシーンの全項目の値（キーも含む）とフレームの速さ・終わりのフレーム。キーのある項目の値は再生位置で決まるので、
/// 比べる前にその状態を記録したときのフレームへ移る（元に戻すは再生位置を動かさない決まり）。
/// </summary>
[TestClass]
public sealed class UndoRedoVerificationTests
{
    private const int Operations = 120;

    private sealed class Rig
    {
        public required SceneDocument Scene { get; init; }
        public required PlaybackService Playback { get; init; }
        public required AnimationService Animation { get; init; }
        public required EditService Edits { get; init; }
        public required ModelNode Model { get; init; }
        public required CameraNode Camera { get; init; }
        public required MorphMixSession Mix { get; init; }
        public KeyClipboard? Clipboard { get; set; }

        public IEnumerable<SceneNode> Keyed => Model.Bones.Cast<SceneNode>().Concat(Model.Morphs).Append(Camera);
    }

    private sealed record Snapshot(NodeState[] Nodes, string[] Names, int FrameRate, int EndFrame, int Frame);

    private static Rig Create()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        playback.EndFrame = 300;
        var edits = new EditService(new UndoService(), animation, playback);
        return new Rig
        {
            Scene = scene,
            Playback = playback,
            Animation = animation,
            Edits = edits,
            Model = model,
            Camera = scene.FindFirst<CameraNode>()!,
            Mix = new MorphMixSession(model, edits),
        };
    }

    private static Snapshot Take(Rig rig)
    {
        SceneNode[] nodes = [.. rig.Scene.AllNodes];
        return new Snapshot([.. nodes.Select(n => n.CaptureState(includeKeys: true))], [.. nodes.Select(n => n.Name)],
            rig.Playback.FrameRate, rig.Playback.EndFrame, rig.Playback.CurrentFrame);
    }

    private static void AssertSame(Snapshot expected, Rig rig, string when)
    {
        rig.Playback.Seek(expected.Frame);
        Snapshot actual = Take(rig);
        var problems = new StringBuilder();
        if (actual.FrameRate != expected.FrameRate || actual.EndFrame != expected.EndFrame)
        {
            problems.AppendLine($"フレームの速さ・終わり: 期待 {expected.FrameRate}/{expected.EndFrame}、実際 {actual.FrameRate}/{actual.EndFrame}");
        }
        if (actual.Nodes.Length != expected.Nodes.Length)
        {
            problems.AppendLine($"項目の数: 期待 {expected.Nodes.Length}、実際 {actual.Nodes.Length}");
        }
        else
        {
            for (int i = 0; i < actual.Nodes.Length; i++)
            {
                if (!actual.Nodes[i].Equals(expected.Nodes[i]))
                {
                    problems.AppendLine($"「{expected.Names[i]}」: 期待 {Describe(expected.Nodes[i])}\n　実際 {Describe(actual.Nodes[i])}");
                }
            }
        }
        if (problems.Length > 0)
        {
            Assert.Fail($"{when}: 状態が合いません\n{problems}");
        }
    }

    private static string Describe(NodeState s) => s.Extra switch
    {
        MorphState m => $"{s} 値 {m.Weight} 未登録 {m.HasUnregisteredChange} キー [{string.Join(", ", m.Keys?.Keys.Select(k => $"{k.Frame}:{k.Weight}") ?? [])}]",
        BoneState b => $"{s} 回転 {b.Orientation} 未登録 {b.HasUnregisteredChange} キー [{string.Join(", ", b.Keys?.Keys.Select(k => $"{k.Frame}") ?? [])}]",
        CameraState c => $"{s} 視野角 {c.FieldOfView} 未登録 {c.HasUnregisteredChange} キー [{string.Join(", ", c.Keys?.Keys.Select(k => $"{k.Frame}") ?? [])}]",
        _ => s.ToString(),
    };

    private static readonly MorphPreset Smile = new("笑顔", [KeyValuePair.Create("まばたき", 1f), KeyValuePair.Create("あ", 0.2f)]);
    private static readonly MorphPreset Surprise = new("驚き", [KeyValuePair.Create("あ", 1f)]);

    /// <summary>でたらめな編集を 1 つ行い、その説明を返す。</summary>
    private static string RandomOperation(Rig rig, Random random)
    {
        EditService edits = rig.Edits;
        BoneNode bone = rig.Model.Bones[random.Next(rig.Model.Bones.Count)];
        MorphNode morph = rig.Model.Morphs[random.Next(rig.Model.Morphs.Count)];
        float Value() => MathF.Round((float)random.NextDouble(), 3);
        SceneNode[] keyed = [.. rig.Keyed];
        IReadOnlyList<KeyRef> AllKeys() => KeyEditing.InRange(keyed, 0, int.MaxValue);

        switch (random.Next(19))
        {
            case 0 when bone.CanEdit:
                // 同じ値を続けて変えると 1 つにまとまる（ドラッグやホイールと同じ）
                edits.Change($"{bone.Name}の移動", [bone], () => bone.Position = new Vector3(Value(), Value(), Value()), mergeKey: $"{bone.Id}:移動");
                return "ボーンの移動";
            case 1 when bone.CanEdit:
                edits.Change($"{bone.Name}の回転", [bone],
                    () => bone.Orientation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(Value(), Value(), Value())));
                return "ボーンの回転";
            case 2 when morph.CanEdit:
                edits.Change($"{morph.Name}の値", [morph], () => morph.Weight = Value(), mergeKey: $"{morph.Id}:値");
                return "モーフの値";
            case 3 when rig.Camera.CanEdit:
                edits.Change("カメラ", [rig.Camera], () =>
                {
                    rig.Camera.FieldOfView = 20 + (Value() * 40);
                    rig.Camera.Position = new Vector3(Value(), 10, -45);
                });
                return "カメラ";
            case 4:
            {
                SceneNode[] nodes = [.. keyed.Where(_ => random.Next(3) == 0)];
                edits.Change("キーの登録", nodes, () => rig.Animation.Register(nodes), includeKeys: true);
                return $"キーの登録（{nodes.Length} 個）";
            }
            case 5:
                edits.Change("未登録の変更をすべて登録", rig.Animation.Unregistered, () => rig.Animation.RegisterUnregistered(), includeKeys: true);
                return "未登録をすべて登録";
            case 6:
                edits.Change("未登録の変更を破棄", rig.Animation.Unregistered, rig.Animation.DiscardUnregistered);
                return "未登録を破棄";
            case 7:
                rig.Playback.Seek(random.Next(0, rig.Playback.EndFrame + 1));
                return "再生位置の移動";
            case 8:
            {
                KeyRef[] keys = [.. AllKeys().Where(_ => random.Next(3) == 0)];
                edits.Change("キーの削除", keys.Select(k => k.Node), () => KeyEditing.Delete(keys), includeKeys: true);
                return $"キーの削除（{keys.Length} 個）";
            }
            case 9:
            {
                KeyRef[] keys = [.. AllKeys().Where(_ => random.Next(2) == 0)];
                int delta = random.Next(-20, 21);
                bool duplicate = random.Next(2) == 0;
                edits.Change("キーの移動", keys.Select(k => k.Node), () => KeyEditing.Move(keys, delta, duplicate), includeKeys: true);
                return $"キーの{(duplicate ? "複製" : "移動")}（{keys.Length} 個、{delta}）";
            }
            case 10:
            {
                KeyRef[] keys = [.. AllKeys().Where(_ => random.Next(2) == 0)];
                var preset = (InterpolationPreset)random.Next(3);
                edits.Change("補間", keys.Select(k => k.Node), () => KeyEditing.SetInterpolation(keys, preset), includeKeys: true);
                return $"補間（{preset}）";
            }
            case 11:
            {
                KeyRef[] keys = [.. AllKeys().Where(_ => random.Next(2) == 0)];
                var curve = BezierCurve.FromMmd((byte)random.Next(128), (byte)random.Next(128), (byte)random.Next(128), (byte)random.Next(128));
                int? channel = random.Next(2) == 0 ? null : 0;
                edits.Change("補間曲線", keys.Select(k => k.Node), () => CurveEditing.SetCurve(keys, channel, curve), includeKeys: true);
                return "補間曲線";
            }
            case 12:
            {
                KeyRef[] keys = [.. AllKeys().Where(_ => random.Next(2) == 0)];
                if (keys.Length > 0)
                {
                    rig.Clipboard = KeyEditing.Copy(keys);
                }
                if (rig.Clipboard is { } clipboard)
                {
                    int frame = random.Next(0, 200);
                    edits.Change("貼り付け", clipboard.Items.Select(i => i.Node), () => KeyEditing.Paste(clipboard, frame), includeKeys: true);
                }
                return "コピーと貼り付け";
            }
            case 13 when bone.CanEdit:
            {
                // ドラッグ: 何度も変えてから 1 つの操作にする。右クリック・Esc なら取り消す
                EditTransaction drag = edits.Begin($"{bone.Name}のドラッグ", [bone]);
                for (int i = 0; i < 4; i++)
                {
                    bone.Position = new Vector3(Value(), Value(), 0);
                }
                bool cancel = random.Next(3) == 0;
                if (cancel)
                {
                    drag.Cancel();
                }
                else
                {
                    drag.Commit();
                }
                return cancel ? "ドラッグの取り消し" : "ドラッグ";
            }
            case 14:
            {
                int rate = PlaybackService.SupportedFrameRates[random.Next(PlaybackService.SupportedFrameRates.Count)];
                bool rescale = random.Next(2) == 0;
                edits.ChangeFrameRate(rate, keyed, rescale);
                return $"フレームの速さ（{rate}、キーも{(rescale ? "合わせる" : "そのまま")}）";
            }
            case 15:
                rig.Animation.AutoKey = !rig.Animation.AutoKey;
                return $"自動キー {(rig.Animation.AutoKey ? "ON" : "OFF")}";
            case 16:
                rig.Mix.SetRatio(random.Next(2) == 0 ? Smile : Surprise, Value());
                return "表情の混合";
            case 17:
            {
                SceneNode target = random.Next(3) switch { 0 => morph, 1 => bone, _ => rig.Model };
                edits.Change("鍵", [target], () => target.IsLocked = !target.IsLocked);
                return $"鍵（{target.Name}）";
            }
            case 18:
            {
                MorphNode[] morphs = [.. rig.Model.Morphs.Where(m => m.CanEdit)];
                edits.Change("モーフをすべて 0 に", morphs, () =>
                {
                    foreach (MorphNode m in morphs)
                    {
                        m.Weight = 0;
                    }
                });
                return "モーフをすべて 0";
            }
            default:
                return "（ロック中のため何もしない）";
        }
    }

    [TestMethod]
    public void RandomEditsUndoAndRedoStepByStep()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            Rig rig = Create();
            var random = new Random(seed);
            // 履歴の数 → その時点の状態（同じ操作にまとまった・何も積まなかったときは、新しいもので置き換える）
            var states = new Dictionary<int, Snapshot> { [0] = Take(rig) };
            var log = new List<string>();
            for (int i = 0; i < Operations; i++)
            {
                log.Add(RandomOperation(rig, random));
                states[rig.Edits.Undo.UndoCount] = Take(rig);
            }
            string history = $"種 {seed} の操作: {string.Join(" → ", log)}";

            int top = rig.Edits.Undo.UndoCount;
            Assert.IsGreaterThan(Operations / 5, top, $"種 {seed}: 履歴に十分な数の操作が積まれている（鍵や空の選択で何もしない回もある）");
            for (int k = top; k > 0; k--)
            {
                string description = rig.Edits.Undo.UndoDescription!;
                Assert.IsTrue(rig.Edits.Undo.Undo());
                AssertSame(states[k - 1], rig, $"{history}\n元に戻す（{k} 番目「{description}」）の後");
            }
            Assert.IsFalse(rig.Edits.Undo.CanUndo);
            for (int k = 1; k <= top; k++)
            {
                string description = rig.Edits.Undo.RedoDescription!;
                Assert.IsTrue(rig.Edits.Undo.Redo());
                AssertSame(states[k], rig, $"{history}\nやり直し（{k} 番目「{description}」）の後");
            }
            Assert.IsFalse(rig.Edits.Undo.CanRedo);

            // 途中まで戻してから新しく編集すると、やり直しの履歴は消え、その前までは戻せる
            for (int k = 0; k < top / 2; k++)
            {
                rig.Edits.Undo.Undo();
            }
            int middle = rig.Edits.Undo.UndoCount;
            CameraNode camera = rig.Camera; // カメラには鍵を掛けていない
            rig.Edits.Change("新しい編集", [camera], () => camera.FieldOfView += 1);
            Assert.IsFalse(rig.Edits.Undo.CanRedo, "新しく編集すると、やり直しの履歴は消える");
            Assert.AreEqual(middle + 1, rig.Edits.Undo.UndoCount);
            rig.Edits.Undo.Undo();
            AssertSame(states[middle], rig, $"{history}\n途中まで戻して新しく編集し、それを戻した後");
            rig.Mix.Dispose();
        }
    }
}
