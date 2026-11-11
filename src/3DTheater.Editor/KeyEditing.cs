using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>キー 1 つを指す（どの項目の、どのフレームのキーか）。項目はボーン・モーフ・カメラ・照明・モデルの表示・IK。</summary>
public readonly record struct KeyRef(SceneNode Node, int Frame);

/// <summary>補間の種類（右クリックのメニューで選ぶ）。</summary>
public enum InterpolationPreset
{
    /// <summary>一定の速さで動く。</summary>
    Linear,

    /// <summary>ゆっくり動き出し、ゆっくり止まる。</summary>
    Smooth,

    /// <summary>次のキーまで値を変えない。</summary>
    Step,
}

/// <summary>コピーしたキー。フレームは、いちばん前のキーからのずれで持つ。</summary>
public sealed class KeyClipboard
{
    internal KeyClipboard(IReadOnlyList<(SceneNode Node, int Offset, object Key)> items) => Items = items;

    internal IReadOnlyList<(SceneNode Node, int Offset, object Key)> Items { get; }

    public int Count => Items.Count;

    /// <summary>貼り付けで変わる項目。</summary>
    public IEnumerable<SceneNode> Nodes => Items.Select(i => i.Node).Distinct();
}

/// <summary>タイムラインで選んでいるキー。</summary>
public sealed class KeySelectionService
{
    private readonly HashSet<KeyRef> _items = [];

    public event EventHandler? Changed;

    public IReadOnlyCollection<KeyRef> Items => _items;

    public int Count => _items.Count;

    public bool Contains(KeyRef key) => _items.Contains(key);

    public void Set(IEnumerable<KeyRef> keys)
    {
        _items.Clear();
        _items.UnionWith(keys);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(IEnumerable<KeyRef> keys)
    {
        int before = _items.Count;
        _items.UnionWith(keys);
        if (_items.Count != before)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Remove(IEnumerable<KeyRef> keys)
    {
        int before = _items.Count;
        _items.ExceptWith(keys);
        if (_items.Count != before)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>もう無いキー（消した・元に戻したなど）を選択から外す。</summary>
    public void Prune()
    {
        if (_items.RemoveWhere(k => !KeyEditing.Exists(k)) > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// キーの編集（移動・複製・削除・コピーと貼り付け・補間の切り替え）。キーの並びを直接書き換えるので、
/// 画面からは <see cref="EditService.Change"/> の中で呼び、元に戻せるようにする。
/// 動かした先に選んでいないキーがあれば、動かしたキーで置き換える。
/// </summary>
public static class KeyEditing
{
    public static bool Exists(KeyRef key) => key.Node switch
    {
        BoneNode bone => bone.Keys.HasKeyAt(key.Frame),
        MorphNode morph => morph.Keys.HasKeyAt(key.Frame),
        CameraNode camera => camera.Keys.HasKeyAt(key.Frame),
        LightNode light => light.Keys.HasKeyAt(key.Frame),
        ModelDisplayNode display => display.Keys.HasKeyAt(key.Frame),
        _ => false,
    };

    /// <summary>キーを持てる項目か（ボーン・モーフ・カメラ・照明・モデルの表示・IK）。</summary>
    public static bool HasKeys(SceneNode node) => node is BoneNode or MorphNode or CameraNode or LightNode or ModelDisplayNode;

    /// <summary>項目のキーの数（キーを持てない項目は 0）。</summary>
    public static int CountKeys(SceneNode node) => node switch
    {
        BoneNode bone => bone.Keys.Count,
        MorphNode morph => morph.Keys.Count,
        CameraNode camera => camera.Keys.Count,
        LightNode light => light.Keys.Count,
        ModelDisplayNode display => display.Keys.Count,
        _ => 0,
    };

    /// <summary>
    /// キーの位置を <paramref name="scale"/> 倍にする（作品のフレームの速さを変えても、動きの秒数を保つとき）。
    /// 同じフレームに重なったキーは後ろのものを使う。広げるときは、元で 1 フレームしか離れていなかったカメラ・照明のキー
    /// （場面の切り替え）を段階の補間にする（間に途中の絵を出さない。VMD の読み込みと同じ）。重なって消えたキーの数を返す。
    /// </summary>
    public static int ScaleTime(IEnumerable<SceneNode> nodes, double scale)
    {
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        int lost = 0;
        foreach (SceneNode node in nodes.Distinct())
        {
            switch (node)
            {
                case BoneNode bone:
                    lost += Replace(bone.Keys, [.. bone.Keys.Keys.Select(k => k with { Frame = Scale(k.Frame) })]);
                    break;
                case MorphNode morph:
                    lost += Replace(morph.Keys, [.. morph.Keys.Keys.Select(k => k with { Frame = Scale(k.Frame) })]);
                    break;
                case CameraNode camera:
                {
                    IReadOnlyList<CameraKey> keys = camera.Keys.Keys;
                    var scaled = new CameraKey[keys.Count];
                    for (int i = 0; i < keys.Count; i++)
                    {
                        bool cut = scale > 1 && i > 0 && keys[i].Frame - keys[i - 1].Frame == 1;
                        scaled[i] = keys[i] with { Frame = Scale(keys[i].Frame), Kind = cut ? KeyInterpolationKind.Step : keys[i].Kind };
                    }
                    lost += Replace(camera.Keys, scaled);
                    break;
                }
                case LightNode light:
                {
                    IReadOnlyList<LightKey> keys = light.Keys.Keys;
                    var scaled = new LightKey[keys.Count];
                    for (int i = 0; i < keys.Count; i++)
                    {
                        bool cut = scale > 1 && i > 0 && keys[i].Frame - keys[i - 1].Frame == 1;
                        scaled[i] = keys[i] with { Frame = Scale(keys[i].Frame), Kind = cut ? KeyInterpolationKind.Step : keys[i].Kind };
                    }
                    lost += Replace(light.Keys, scaled);
                    break;
                }
                case ModelDisplayNode display:
                    lost += Replace(display.Keys, [.. display.Keys.Keys.Select(k => k with { Frame = Scale(k.Frame) })]);
                    break;
            }
        }
        return lost;

        static int Replace<TKey>(KeyTrack<TKey> track, TKey[] keys) where TKey : struct, IKeyframe
        {
            int lostHere = keys.Length - keys.Select(k => k.Frame).Distinct().Count();
            track.ReplaceAll(keys);
            return lostHere;
        }
    }

    /// <summary>まとまり（モデル・表示枠など）の中で、<paramref name="frame"/> にあるキーすべて。</summary>
    public static IReadOnlyList<KeyRef> AtFrame(SceneNode node, int frame) =>
        [.. node.DescendantsAndSelf().Where(HasKeys).Select(n => new KeyRef(n, frame)).Where(Exists)];

    /// <summary>項目（まとまりなら中の項目すべて）の、<paramref name="first"/>〜<paramref name="last"/> フレームのキー。</summary>
    public static IReadOnlyList<KeyRef> InRange(IEnumerable<SceneNode> rows, int first, int last)
    {
        var result = new List<KeyRef>();
        foreach (SceneNode node in rows.SelectMany(r => r.DescendantsAndSelf()).Distinct())
        {
            switch (node)
            {
                case BoneNode bone:
                    Collect(bone, bone.Keys);
                    break;
                case MorphNode morph:
                    Collect(morph, morph.Keys);
                    break;
                case CameraNode camera:
                    Collect(camera, camera.Keys);
                    break;
                case LightNode light:
                    Collect(light, light.Keys);
                    break;
                case ModelDisplayNode display:
                    Collect(display, display.Keys);
                    break;
            }
        }
        return result;

        void Collect<TKey>(SceneNode owner, KeyTrack<TKey> track) where TKey : struct, IKeyframe
        {
            var (start, end) = track.Range(first, last);
            for (int i = start; i < end; i++)
            {
                result.Add(new KeyRef(owner, track.Keys[i].Frame));
            }
        }
    }

    /// <summary>動かした後にフレームが 0 より前にならないよう、ずれを抑える。</summary>
    public static int ClampDelta(IEnumerable<KeyRef> keys, int delta)
    {
        int min = keys.Select(k => k.Frame).DefaultIfEmpty(0).Min();
        return Math.Max(delta, -min);
    }

    /// <summary>キーを <paramref name="delta"/> フレーム動かす（<paramref name="duplicate"/> なら元を残して複製する）。動かした後のキーを返す。</summary>
    public static IReadOnlyList<KeyRef> Move(IReadOnlyCollection<KeyRef> keys, int delta, bool duplicate)
    {
        delta = ClampDelta(keys, delta);
        var result = new List<KeyRef>();
        foreach (var group in keys.Where(Exists).GroupBy(k => k.Node))
        {
            var frames = group.Select(k => k.Frame).ToHashSet();
            switch (group.Key)
            {
                case BoneNode bone:
                    MoveIn(bone.Keys, frames, delta, duplicate, (k, f) => k with { Frame = f });
                    break;
                case MorphNode morph:
                    MoveIn(morph.Keys, frames, delta, duplicate, (k, f) => k with { Frame = f });
                    break;
                case CameraNode camera:
                    MoveIn(camera.Keys, frames, delta, duplicate, (k, f) => k with { Frame = f });
                    break;
                case LightNode light:
                    MoveIn(light.Keys, frames, delta, duplicate, (k, f) => k with { Frame = f });
                    break;
                case ModelDisplayNode display:
                    MoveIn(display.Keys, frames, delta, duplicate, (k, f) => k with { Frame = f });
                    break;
            }
            result.AddRange(frames.Select(f => new KeyRef(group.Key, f + delta)));
        }
        return result;
    }

    public static int Delete(IEnumerable<KeyRef> keys)
    {
        int count = 0;
        foreach (var group in keys.Where(Exists).GroupBy(k => k.Node))
        {
            var frames = group.Select(k => k.Frame).ToHashSet();
            count += frames.Count;
            switch (group.Key)
            {
                case BoneNode bone:
                    bone.Keys.ReplaceAll(bone.Keys.Keys.Where(k => !frames.Contains(k.Frame)));
                    break;
                case MorphNode morph:
                    morph.Keys.ReplaceAll(morph.Keys.Keys.Where(k => !frames.Contains(k.Frame)));
                    break;
                case CameraNode camera:
                    camera.Keys.ReplaceAll(camera.Keys.Keys.Where(k => !frames.Contains(k.Frame)));
                    break;
                case LightNode light:
                    light.Keys.ReplaceAll(light.Keys.Keys.Where(k => !frames.Contains(k.Frame)));
                    break;
                case ModelDisplayNode display:
                    display.Keys.ReplaceAll(display.Keys.Keys.Where(k => !frames.Contains(k.Frame)));
                    break;
            }
        }
        return count;
    }

    public static KeyClipboard Copy(IEnumerable<KeyRef> keys)
    {
        KeyRef[] existing = [.. keys.Where(Exists)];
        int origin = existing.Select(k => k.Frame).DefaultIfEmpty(0).Min();
        var items = new List<(SceneNode, int, object)>();
        foreach (KeyRef k in existing.OrderBy(k => k.Frame))
        {
            switch (k.Node)
            {
                case BoneNode bone when bone.Keys.TryGet(k.Frame, out BoneKey key):
                    items.Add((bone, k.Frame - origin, key));
                    break;
                case MorphNode morph when morph.Keys.TryGet(k.Frame, out MorphKey key):
                    items.Add((morph, k.Frame - origin, key));
                    break;
                case CameraNode camera when camera.Keys.TryGet(k.Frame, out CameraKey key):
                    items.Add((camera, k.Frame - origin, key));
                    break;
                case LightNode light when light.Keys.TryGet(k.Frame, out LightKey key):
                    items.Add((light, k.Frame - origin, key));
                    break;
                case ModelDisplayNode display when display.Keys.TryGet(k.Frame, out DisplayKey key):
                    items.Add((display, k.Frame - origin, key));
                    break;
            }
        }
        return new KeyClipboard(items);
    }

    /// <summary>コピーしたキーを、いちばん前のキーが <paramref name="frame"/> に来るように貼る。貼ったキーを返す。</summary>
    public static IReadOnlyList<KeyRef> Paste(KeyClipboard clipboard, int frame)
    {
        var result = new List<KeyRef>();
        foreach (var group in clipboard.Items.GroupBy(i => i.Node))
        {
            switch (group.Key)
            {
                case BoneNode bone:
                    bone.Keys.SetRange(group.Select(i => ((BoneKey)i.Key) with { Frame = frame + i.Offset }));
                    break;
                case MorphNode morph:
                    morph.Keys.SetRange(group.Select(i => ((MorphKey)i.Key) with { Frame = frame + i.Offset }));
                    break;
                case CameraNode camera:
                    camera.Keys.SetRange(group.Select(i => ((CameraKey)i.Key) with { Frame = frame + i.Offset }));
                    break;
                case LightNode light:
                    light.Keys.SetRange(group.Select(i => ((LightKey)i.Key) with { Frame = frame + i.Offset }));
                    break;
                case ModelDisplayNode display:
                    display.Keys.SetRange(group.Select(i => ((DisplayKey)i.Key) with { Frame = frame + i.Offset }));
                    break;
            }
            result.AddRange(group.Select(i => new KeyRef(group.Key, frame + i.Offset)));
        }
        return result;
    }

    /// <summary>キーの補間（前のキーからこのキーまでの動き方）を変える。表示・IK のキーはいつも段階なので変えない。</summary>
    public static void SetInterpolation(IEnumerable<KeyRef> keys, InterpolationPreset preset)
    {
        BezierCurve curve = preset == InterpolationPreset.Smooth ? SmoothCurve : BezierCurve.Linear;
        KeyInterpolationKind kind = preset == InterpolationPreset.Step ? KeyInterpolationKind.Step : KeyInterpolationKind.Curve;
        foreach (var group in keys.Where(Exists).GroupBy(k => k.Node))
        {
            var frames = group.Select(k => k.Frame).ToHashSet();
            switch (group.Key)
            {
                case BoneNode bone:
                    bone.Keys.ReplaceAll(bone.Keys.Keys.Select(k => frames.Contains(k.Frame)
                        ? k with { Interpolation = new BoneInterpolation(curve, curve, curve, curve), Kind = kind }
                        : k));
                    break;
                case MorphNode morph:
                    morph.Keys.ReplaceAll(morph.Keys.Keys.Select(k => frames.Contains(k.Frame) ? k with { Curve = curve, Kind = kind } : k));
                    break;
                case CameraNode camera:
                    camera.Keys.ReplaceAll(camera.Keys.Keys.Select(k => frames.Contains(k.Frame)
                        ? k with { Interpolation = new CameraInterpolation(curve, curve, curve, curve, curve, curve), Kind = kind }
                        : k));
                    break;
                case LightNode light:
                    light.Keys.ReplaceAll(light.Keys.Keys.Select(k => frames.Contains(k.Frame) ? k with { Curve = curve, Kind = kind } : k));
                    break;
            }
        }
    }

    /// <summary>「なめらか」の曲線（ゆっくり動き出し、ゆっくり止まる）。</summary>
    public static BezierCurve SmoothCurve { get; } = BezierCurve.FromMmd(64, 0, 64, 127);

    private static void MoveIn<TKey>(KeyTrack<TKey> track, HashSet<int> frames, int delta, bool duplicate, Func<TKey, int, TKey> withFrame)
        where TKey : struct, IKeyframe
    {
        TKey[] moved = [.. track.Keys.Where(k => frames.Contains(k.Frame)).Select(k => withFrame(k, k.Frame + delta))];
        var destinations = moved.Select(k => k.Frame).ToHashSet();
        IEnumerable<TKey> remaining = track.Keys.Where(k => (duplicate || !frames.Contains(k.Frame)) && !destinations.Contains(k.Frame));
        track.ReplaceAll(remaining.Concat(moved));
    }
}
