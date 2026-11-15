using System.Text;
using System.Text.RegularExpressions;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX のボーンの名前から、VRM の人型のボーン（hips・spine・head・leftUpperArm など）を推し量る（設計判断記録 83 章の 2 段目）。
/// FBX には人型の決まりが無いので、よくある名前の付け方を読む。
/// <list type="bullet">
/// <item>Mixamo（mixamorig:Hips・LeftArm・LeftForeArm・LeftUpLeg・LeftLeg・LeftHandIndex1）</item>
/// <item>Unity・VRChat（Hips・UpperArm.L・LowerLeg_R・Left arm・Left knee・Thumb Proximal.L・IndexFinger1_L）</item>
/// <item>VRoid（J_Bip_C_Hips・J_Bip_L_UpperArm・J_Adj_L_FaceEye）</item>
/// <item>Blender（upper_arm.L・forearm.L・thigh.L・shin.L・f_index.01.L）</item>
/// <item>3ds Max の Biped（Bip01 Pelvis・Bip01 L Clavicle・Bip01 L Calf・Bip01 L Finger0）</item>
/// </list>
/// 名前を語に分け（区切りと大文字・数字の境目）、左右の語（L・Left・R・Right）と飾りの語（J・Bip・Adj・C）を除いた残りで決める。
/// ねじれ・先端・揺れ物などの補助のボーン（twist・end・roll・aim・sec など）は使わない。同じ名前のボーンが幾つかあれば、
/// 親子の決まり（腕は肩の下、ひじは腕の下など）に合うもののうち、腰にいちばん近いものを使う。
/// </summary>
internal static class FbxHumanoid
{
    /// <summary>人型と見なすのに要るボーン（腰・背骨・頭）。</summary>
    public static readonly string[] Required = ["hips", "spine", "head"];

    /// <summary>見つからなければレポートに書くボーン（指・目・つま先・上半身2 などは、無いモデルが多いので書かない）。</summary>
    public static readonly string[] Main =
    [
        "neck", "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm", "leftHand", "rightHand",
        "leftUpperLeg", "rightUpperLeg", "leftLowerLeg", "rightLowerLeg", "leftFoot", "rightFoot",
    ];

    private static readonly HashSet<string> Decorations = new(StringComparer.Ordinal)
    {
        "j", "bip", "adj", "c", "def", "mixamorig", "f", "b", "bone", "jnt", "joint", "valvebiped", "center",
    };

    private static readonly HashSet<string> Helpers = new(StringComparer.Ordinal)
    {
        "end", "nub", "tip", "twist", "roll", "aim", "sec", "ik", "pole", "target", "helper", "dummy", "null", "ctrl", "control",
        "fk", "mch", "org", "corrective",
    };

    private static readonly Dictionary<string, string> CenterBones = new(StringComparer.Ordinal)
    {
        ["hips"] = "hips", ["hip"] = "hips", ["pelvis"] = "hips",
        ["chest"] = "chest", ["upperchest"] = "upperChest",
        ["neck"] = "neck", ["head"] = "head",
    };

    private static readonly Dictionary<string, string> SideBones = new(StringComparer.Ordinal)
    {
        ["shoulder"] = "Shoulder", ["clavicle"] = "Shoulder", ["collar"] = "Shoulder", ["collarbone"] = "Shoulder",
        ["upperarm"] = "UpperArm", ["arm"] = "UpperArm", ["uparm"] = "UpperArm",
        ["lowerarm"] = "LowerArm", ["forearm"] = "LowerArm", ["elbow"] = "LowerArm", ["lowarm"] = "LowerArm",
        ["hand"] = "Hand", ["wrist"] = "Hand",
        ["upperleg"] = "UpperLeg", ["upleg"] = "UpperLeg", ["thigh"] = "UpperLeg",
        ["lowerleg"] = "LowerLeg", ["knee"] = "LowerLeg", ["shin"] = "LowerLeg", ["calf"] = "LowerLeg", ["lowleg"] = "LowerLeg",
        ["leg"] = "Leg", // Mixamo はひざ（LeftLeg）、VRChat は太もも（Left leg）。あとで決める
        ["foot"] = "Foot", ["ankle"] = "Foot",
        ["toe"] = "Toes", ["toes"] = "Toes", ["toebase"] = "Toes",
        ["eye"] = "Eye", ["faceeye"] = "Eye",
    };

    private static readonly Dictionary<string, string> Fingers = new(StringComparer.Ordinal)
    {
        ["thumb"] = "Thumb", ["index"] = "Index", ["middle"] = "Middle", ["ring"] = "Ring", ["little"] = "Little", ["pinky"] = "Little", ["pinkie"] = "Little",
        ["finger"] = string.Empty,
    };

    private static readonly HashSet<string> Segments = new(StringComparer.Ordinal) { "proximal", "intermediate", "distal", "metacarpal" };

    private static readonly Regex BipedPrefix = new(@"^(valvebiped\.)?bip0*1[ _]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>名前を分けた結果（左右・語・最後の数・補助のボーンか）。</summary>
    internal readonly record struct ParsedName(char Side, string Key, int? Number, bool Helper);

    /// <summary>名前を語に分ける。左右は 'L'・'R'、無ければ ' '。</summary>
    internal static ParsedName Parse(string raw)
    {
        string name = raw;
        int cut = Math.Max(name.LastIndexOf(':'), name.LastIndexOf('|'));
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }
        name = BipedPrefix.Replace(name, string.Empty);
        var words = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
        }
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            if (current.Length > 0)
            {
                char prev = name[i - 1];
                bool boundary = (char.IsLower(prev) && char.IsUpper(c))
                    || (char.IsDigit(prev) != char.IsDigit(c))
                    || (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]));
                if (boundary)
                {
                    Flush();
                }
            }
            current.Append(c);
        }
        Flush();

        char side = ' ';
        bool helper = false;
        int? number = null;
        var key = new StringBuilder();
        foreach (string w in words)
        {
            if (w is "l" or "left")
            {
                side = 'L';
            }
            else if (w is "r" or "right")
            {
                side = 'R';
            }
            else if (char.IsDigit(w[0]))
            {
                number = int.TryParse(w, out int n) ? n : null;
            }
            else if (Helpers.Contains(w))
            {
                helper = true;
            }
            else if (!Decorations.Contains(w))
            {
                key.Append(w);
            }
        }
        return new ParsedName(side, key.ToString(), number, helper);
    }

    /// <summary>
    /// 人型のボーンを推し量る。<paramref name="parents"/> はノードごとの親の番号（無ければ -1）。腰・背骨・頭が見つからなければ空を返す。
    /// <paramref name="missing"/> は、見つからなかった主なボーン（VRM の名前）。
    /// </summary>
    public static Dictionary<string, int> Guess(IReadOnlyList<string> names, IReadOnlyList<int> parents, out string[] missing)
    {
        int count = names.Count;
        int[] depth = new int[count];
        for (int i = 0; i < count; i++)
        {
            int d = 0;
            for (int p = parents[i]; p >= 0 && d <= count; p = parents[p])
            {
                d++;
            }
            depth[i] = d;
        }
        bool IsBelow(int node, int ancestor)
        {
            int guard = 0;
            for (int p = parents[node]; p >= 0 && guard++ <= count; p = parents[p])
            {
                if (p == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        // 名前ごとの候補（VRM の名前 → ノード）
        var candidates = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var fingerCandidates = new Dictionary<string, List<int>>(StringComparer.Ordinal); // "leftIndex" など
        void Add(Dictionary<string, List<int>> map, string bone, int node)
        {
            if (!map.TryGetValue(bone, out List<int>? list))
            {
                list = [];
                map[bone] = list;
            }
            list.Add(node);
        }
        for (int i = 0; i < count; i++)
        {
            ParsedName parsed = Parse(names[i]);
            if (parsed.Helper || parsed.Key.Length == 0)
            {
                continue;
            }
            string sidePrefix = parsed.Side switch { 'L' => "left", 'R' => "right", _ => string.Empty };
            if (FingerOf(parsed.Key) is { } finger)
            {
                if (sidePrefix.Length > 0)
                {
                    Add(fingerCandidates, sidePrefix + finger, i);
                }
                continue;
            }
            if (sidePrefix.Length == 0)
            {
                if (parsed.Key == "spine")
                {
                    // Mixamo・Biped: Spine・Spine1・Spine2 は 背骨・胸・胸の上（Spine3 などがあれば、いちばん上のものを胸の上にする）
                    Add(candidates, parsed.Number switch { null or 0 => "spine", 1 => "chest", _ => "upperChest" }, i);
                }
                else if (CenterBones.TryGetValue(parsed.Key, out string? center))
                {
                    Add(candidates, center, i);
                }
            }
            else if (SideBones.TryGetValue(parsed.Key, out string? part))
            {
                Add(candidates, sidePrefix + part, i);
            }
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        // 候補のうち、ancestor の下にあって、まだ使っていないもので、腰にいちばん近いもの
        int Pick(string bone, int ancestor, bool deepest = false)
        {
            if (!candidates.TryGetValue(bone, out List<int>? list))
            {
                return -1;
            }
            int best = -1;
            foreach (int node in list)
            {
                if (!taken.Contains(node) && (ancestor < 0 || IsBelow(node, ancestor))
                    && (best < 0 || (deepest ? depth[node] > depth[best] : depth[node] < depth[best])))
                {
                    best = node;
                }
            }
            return best;
        }
        bool Set(string bone, int node)
        {
            if (node < 0)
            {
                return false;
            }
            result[bone] = node;
            taken.Add(node);
            return true;
        }
        int Get(string bone) => result.GetValueOrDefault(bone, -1);
        int FirstOf(params string[] bones) => bones.Select(Get).FirstOrDefault(n => n >= 0, -1);

        // 腰: 下にいちばん多くのボーンを持つもの
        if (candidates.TryGetValue("hips", out List<int>? hipsList))
        {
            int[] below = new int[count];
            for (int i = 0; i < count; i++)
            {
                for (int p = parents[i], guard = 0; p >= 0 && guard++ <= count; p = parents[p])
                {
                    below[p]++;
                }
            }
            Set("hips", hipsList.OrderByDescending(n => below[n]).ThenBy(n => depth[n]).First());
        }
        int hips = Get("hips");
        if (hips < 0)
        {
            missing = [.. Required];
            return [];
        }

        // 背骨: 背骨が無く胸があれば、胸を背骨にする
        if (!Set("spine", Pick("spine", hips)))
        {
            if (Set("spine", Pick("chest", hips)))
            {
                candidates.Remove("chest");
                if (candidates.Remove("upperChest", out List<int>? upper))
                {
                    candidates["chest"] = upper;
                }
            }
        }
        int spine = Get("spine");
        if (spine >= 0)
        {
            Set("chest", Pick("chest", spine));
            if (Get("chest") >= 0)
            {
                Set("upperChest", Pick("upperChest", Get("chest"), deepest: true));
            }
            int top = FirstOf("upperChest", "chest", "spine");
            Set("neck", Pick("neck", top));
            Set("head", Pick("head", FirstOf("neck", "upperChest", "chest", "spine")));
        }
        int head = Get("head");
        foreach (string side in new[] { "left", "right" })
        {
            if (head >= 0)
            {
                Set(side + "Eye", Pick(side + "Eye", head));
            }
            if (spine >= 0)
            {
                int body = FirstOf("upperChest", "chest", "spine");
                Set(side + "Shoulder", Pick(side + "Shoulder", body));
                if (Set(side + "UpperArm", Pick(side + "UpperArm", FirstOf(side + "Shoulder", "upperChest", "chest", "spine")))
                    && Set(side + "LowerArm", Pick(side + "LowerArm", Get(side + "UpperArm"))))
                {
                    Set(side + "Hand", Pick(side + "Hand", Get(side + "LowerArm")));
                }
            }
            // 足: 「leg」は、太ももの名前（UpLeg・Thigh など）があればひざ、無ければ太もも
            bool legIsLower = candidates.ContainsKey(side + "UpperLeg");
            if (candidates.Remove(side + "Leg", out List<int>? legs))
            {
                string target = side + (legIsLower ? "LowerLeg" : "UpperLeg");
                if (candidates.TryGetValue(target, out List<int>? existing))
                {
                    existing.AddRange(legs);
                }
                else
                {
                    candidates[target] = legs;
                }
            }
            if (Set(side + "UpperLeg", Pick(side + "UpperLeg", hips))
                && Set(side + "LowerLeg", Pick(side + "LowerLeg", Get(side + "UpperLeg")))
                && Set(side + "Foot", Pick(side + "Foot", Get(side + "LowerLeg"))))
            {
                Set(side + "Toes", Pick(side + "Toes", Get(side + "Foot")));
            }
            if (Get(side + "Hand") is int hand and >= 0)
            {
                AddFingers(side, hand);
            }
        }

        // 指: 手首の下の同じ指のボーンを、根元から 3 つまで（4 つ目は先端の目印が多い）
        void AddFingers(string side, int hand)
        {
            foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                if (!fingerCandidates.TryGetValue(side + finger, out List<int>? list))
                {
                    continue;
                }
                string[] parts = finger == "Thumb" ? ["Metacarpal", "Proximal", "Distal"] : ["Proximal", "Intermediate", "Distal"];
                int previous = hand;
                for (int k = 0; k < parts.Length; k++)
                {
                    int best = -1;
                    foreach (int node in list)
                    {
                        if (!taken.Contains(node) && IsBelow(node, previous) && (best < 0 || depth[node] < depth[best]))
                        {
                            best = node;
                        }
                    }
                    if (best < 0)
                    {
                        break;
                    }
                    Set(side + finger + parts[k], best);
                    previous = best;
                }
            }
        }

        missing = [.. Required.Concat(Main).Where(b => !result.ContainsKey(b))];
        if (Required.Any(b => !result.ContainsKey(b)))
        {
            return [];
        }
        return result;
    }

    // 指の名前（親指・人指・中指・薬指・小指）。「Hand」「Finger」と、根元・中・先の語は除いて読む
    private static string? FingerOf(string key)
    {
        string rest = key.StartsWith("hand", StringComparison.Ordinal) && key.Length > 4 ? key[4..] : key;
        foreach (var (word, finger) in Fingers)
        {
            if (finger.Length == 0 || !rest.StartsWith(word, StringComparison.Ordinal))
            {
                continue;
            }
            string tail = rest[word.Length..];
            if (tail.StartsWith("finger", StringComparison.Ordinal))
            {
                tail = tail[6..];
            }
            if (tail.Length == 0 || Segments.Contains(tail))
            {
                return finger;
            }
        }
        return null;
    }
}
