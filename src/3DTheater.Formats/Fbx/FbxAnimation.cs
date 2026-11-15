using System.Numerics;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX のアニメーション（設計判断記録 85 章）。テイク（AnimationStack）の 1 つ目のレイヤー（AnimationLayer）の、ノードごとの移動・回転・大きさの
/// 曲線（AnimationCurveNode の T・R・S と、その X・Y・Z の AnimationCurve）を読み、好きな時刻の値を求める（キーの間はまっすぐ結ぶ）。
/// 時刻は FBX の単位（1 秒 = 46,186,158,000）。
/// </summary>
internal sealed class FbxAnimation
{
    public const double TicksPerSecond = 46186158000.0;

    private readonly Dictionary<int, Channel[]> _channels; // ノードの番号 → [移動, 回転, 大きさ]（無いものは null）

    private FbxAnimation(string name, double start, double stop, int stackCount, int layerCount, Dictionary<int, Channel[]> channels)
    {
        Name = name;
        Start = start;
        Stop = stop;
        StackCount = stackCount;
        LayerCount = layerCount;
        _channels = channels;
    }

    /// <summary>テイクの名前。</summary>
    public string Name { get; }

    public double Start { get; }

    public double Stop { get; }

    /// <summary>ファイルの中のテイクの数（使うのは 1 つ目の、動くノードのあるもの）。</summary>
    public int StackCount { get; }

    /// <summary>使うテイクのレイヤーの数（使うのは 1 つ目）。</summary>
    public int LayerCount { get; }

    /// <summary>動くノードの数。</summary>
    public int AnimatedNodes => _channels.Count;

    public double Seconds => (Stop - Start) / TicksPerSecond;

    /// <summary>
    /// アニメーションを読む。<paramref name="models"/> はノード（番号は <see cref="Sample"/> の番号）。動くノードのあるテイクが無ければ null。
    /// </summary>
    public static FbxAnimation? Read(FbxScene scene, IReadOnlyList<FbxObject> models)
    {
        FbxObject[] stacks = [.. scene.OfKind("AnimationStack")];
        foreach (FbxObject stack in stacks)
        {
            FbxObject[] layers = [.. stack.ChildrenOf("AnimationLayer")];
            if (layers.Length == 0)
            {
                continue;
            }
            var inLayer = new HashSet<long>(layers[0].ChildrenOf("AnimationCurveNode").Select(n => n.Id));
            var channels = new Dictionary<int, Channel[]>();
            double first = double.MaxValue;
            double last = double.MinValue;
            for (int i = 0; i < models.Count; i++)
            {
                foreach ((FbxObject node, string property) in models[i].PropertyChildren)
                {
                    int slot = property switch
                    {
                        "Lcl Translation" => 0,
                        "Lcl Rotation" => 1,
                        "Lcl Scaling" => 2,
                        _ => -1,
                    };
                    if (slot < 0 || node.Kind != "AnimationCurveNode" || !inLayer.Contains(node.Id))
                    {
                        continue;
                    }
                    Vector3 fallback = models[i].Vector(property, slot == 2 ? Vector3.One : Vector3.Zero);
                    var channel = Channel.Read(node, fallback);
                    if (channel is null)
                    {
                        continue;
                    }
                    if (!channels.TryGetValue(i, out Channel[]? slots))
                    {
                        slots = new Channel[3];
                        channels[i] = slots;
                    }
                    slots[slot] = channel;
                    first = Math.Min(first, channel.FirstTime);
                    last = Math.Max(last, channel.LastTime);
                }
            }
            if (channels.Count == 0)
            {
                continue;
            }
            double start = stack.Number("LocalStart", double.NaN);
            double stop = stack.Number("LocalStop", double.NaN);
            if (!(stop > start) || double.IsNaN(start))
            {
                (start, stop) = (first, last);
            }
            return new FbxAnimation(stack.Name, start, Math.Max(start, stop), stacks.Length, layers.Length, channels);
        }
        return null;
    }

    /// <summary>ノードの、時刻 <paramref name="time"/> の移動・回転（度）・大きさ。動かないものは null（ノードに書かれた値を使う）。</summary>
    public (Vector3? Translation, Vector3? Rotation, Vector3? Scaling) Sample(int model, double time) =>
        _channels.TryGetValue(model, out Channel[]? c)
            ? (c[0]?.Sample(time), c[1]?.Sample(time), c[2]?.Sample(time))
            : (null, null, null);

    public bool IsAnimated(int model) => _channels.ContainsKey(model);

    // X・Y・Z の 3 本の曲線（無い成分は、決まった値のまま）
    private sealed class Channel(Curve?[] curves, Vector3 defaults)
    {
        public double FirstTime => curves.Where(c => c is not null).Select(c => c!.FirstTime).DefaultIfEmpty(0).Min();

        public double LastTime => curves.Where(c => c is not null).Select(c => c!.LastTime).DefaultIfEmpty(0).Max();

        public static Channel? Read(FbxObject node, Vector3 fallback)
        {
            var curves = new Curve?[3];
            string[] names = ["d|X", "d|Y", "d|Z"];
            for (int k = 0; k < 3; k++)
            {
                foreach ((FbxObject child, string property) in node.PropertyChildren)
                {
                    if (child.Kind == "AnimationCurve" && property == names[k])
                    {
                        curves[k] = Curve.Read(child);
                        break;
                    }
                }
            }
            if (curves.All(c => c is null))
            {
                return null;
            }
            var defaults = new Vector3((float)node.Number("d|X", fallback.X), (float)node.Number("d|Y", fallback.Y), (float)node.Number("d|Z", fallback.Z));
            return new Channel(curves, defaults);
        }

        public Vector3 Sample(double time) => new(
            curves[0]?.Sample(time) ?? defaults.X,
            curves[1]?.Sample(time) ?? defaults.Y,
            curves[2]?.Sample(time) ?? defaults.Z);
    }

    // 1 本の曲線（キーの時刻と値。キーの間はまっすぐ結び、前後ははしの値）
    private sealed class Curve(double[] times, float[] values)
    {
        public double FirstTime => times.Length > 0 ? times[0] : 0;

        public double LastTime => times.Length > 0 ? times[^1] : 0;

        public static Curve? Read(FbxObject curve)
        {
            double[] times = curve.Node.ChildDoubles("KeyTime") ?? [];
            double[] values = curve.Node.ChildDoubles("KeyValueFloat") ?? curve.Node.ChildDoubles("KeyValueDouble") ?? [];
            int count = Math.Min(times.Length, values.Length);
            if (count == 0)
            {
                double value = curve.Node.ChildDouble("Default", double.NaN);
                return double.IsNaN(value) ? null : new Curve([0], [(float)value]);
            }
            // 時刻の順に並んでいない・同じ時刻のキーは、並べ直して後のものを使う
            var keys = new SortedDictionary<double, float>();
            for (int i = 0; i < count; i++)
            {
                keys[times[i]] = (float)values[i];
            }
            return new Curve([.. keys.Keys], [.. keys.Values]);
        }

        public float Sample(double time)
        {
            if (time <= times[0])
            {
                return values[0];
            }
            if (time >= times[^1])
            {
                return values[^1];
            }
            int index = Array.BinarySearch(times, time);
            if (index >= 0)
            {
                return values[index];
            }
            int next = ~index;
            double t = (time - times[next - 1]) / (times[next] - times[next - 1]);
            return (float)(values[next - 1] + ((values[next] - values[next - 1]) * t));
        }
    }
}
