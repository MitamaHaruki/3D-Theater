using System.Text;
using System.Text.RegularExpressions;
using Theater3D.Formats.Gltf;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX のブレンドシェイプ（表情の元）の名前から、VRM の決まった表情（aa・blink・happy など）を推し量る（設計判断記録 83 章の 3 段目）。
/// その先は VRM と同じく、MMD の表情の名前（あ・まばたき・笑い など）の表情にする（<see cref="VrmExpressions"/>）。
/// <list type="bullet">
/// <item>名前の最後の「.」より前（Maya のブレンドシェイプの節の名前・VRChat の「vrc.」・VRoid の「Face_Blendshape.」）は除き、小文字にして記号を除いて読む。</item>
/// <item>口: VRChat（v_aa・v_ih・v_ou・v_e・v_oh）、VRoid（Fcl_MTH_A〜O）、mouth_a・lip_a・mth_a、a・i・u・e・o。</item>
/// <item>目: Blink・Blink_L・vrc.blink_left、VRoid（Fcl_EYE_Close・_L・_R）、eye_close・eye_blink・eye_winkL、iPhone の顔の動き（eyeBlinkLeft）。</item>
/// <item>気持ち: VRoid（Fcl_ALL_Joy・Fun・Angry・Sorrow・Surprised）、face_happy・joy・relax・angry・sad・surprise、eye_smile（笑い）。</item>
/// <item>目線: look_up・look_down・look_left・look_right（目のボーンが無いモデルは、目線をこの表情で動かす）。</item>
/// </list>
/// 同じ表情に幾つかの名前が当てはまれば、決まった付け方（VRoid・VRChat）のものを先に使う。同じ名前の表情の元が幾つかの形にあれば、すべて使う。
/// 表情の元の名前がもう MMD の名前（まばたき・あ など）なら、その表情は作らない（そのまま使える）。
/// </summary>
internal static class FbxExpressions
{
    private static readonly Regex Vowel = new(@"^(?<p>vrcv|v|fclmth|mth|mouth|lip|lips)?(?<v>aa|ih|ou|ee|oh|a|i|u|e|o)$", RegexOptions.CultureInvariant);

    private static readonly Regex Eye = new(@"^(?<p>vrc|fcleye|eye|eyes)?(?<b>blink|close|wink)(?<s>l|left|r|right)?$", RegexOptions.CultureInvariant);

    private static readonly Regex Feeling = new(@"^(?<p>fclall|face|all)?(?<w>joy|happy|fun|relax|relaxed|angry|anger|sad|sorrow|surprised|surprise)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Look = new(@"^(?<p>look|eyelook|eyeslook)(?<d>up|down|left|right)$", RegexOptions.CultureInvariant);

    /// <summary>名前から、決まった表情（VRM 1.0 の名前）と、その名前の確かさ（小さいほど先に使う）。当てはまらなければ null。</summary>
    internal static (string Key, int Priority)? Classify(string name)
    {
        int dot = name.LastIndexOf('.');
        string tail = dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        var text = new StringBuilder(tail.Length);
        foreach (char c in tail)
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }
        string n = text.ToString();
        if (n.Length == 0)
        {
            return null;
        }
        if (Vowel.Match(n) is { Success: true } vowel)
        {
            string key = vowel.Groups["v"].Value switch
            {
                "a" or "aa" => "aa",
                "i" or "ih" => "ih",
                "u" or "ou" => "ou",
                "e" or "ee" => "ee",
                _ => "oh",
            };
            int priority = vowel.Groups["p"].Value switch
            {
                "v" or "vrcv" or "fclmth" => 1,
                "" => 3,
                _ => 2,
            };
            return (key, priority);
        }
        if (n == "jawopen")
        {
            return ("aa", 5); // iPhone の顔の動き: 口を開く
        }
        if (Eye.Match(n) is { Success: true } eye)
        {
            string prefix = eye.Groups["p"].Value;
            string body = eye.Groups["b"].Value;
            string side = eye.Groups["s"].Value;
            if ((body == "close" && prefix.Length == 0) || (body == "wink" && side.Length == 0))
            {
                return null; // 「Close」「Wink」だけでは、目か口か・どちらの目か分からない
            }
            string key = side switch
            {
                "l" or "left" => "blinkLeft",
                "r" or "right" => "blinkRight",
                _ => "blink",
            };
            int priority = prefix is "vrc" or "fcleye" || (prefix.Length == 0 && body == "blink") ? 1 : body == "wink" ? 3 : 2;
            return (key, priority);
        }
        if (Feeling.Match(n) is { Success: true } feeling)
        {
            string key = feeling.Groups["w"].Value switch
            {
                "joy" or "happy" => "happy",
                "fun" or "relax" or "relaxed" => "relaxed",
                "angry" or "anger" => "angry",
                "sad" or "sorrow" => "sad",
                _ => "surprised",
            };
            return (key, feeling.Groups["p"].Value == "fclall" ? 1 : 2);
        }
        if (n is "eyesmile" or "eyessmile")
        {
            return ("happy", 3); // 目の笑い（MMD の「笑い」も目の形）
        }
        if (Look.Match(n) is { Success: true } look)
        {
            string d = look.Groups["d"].Value;
            return ("look" + char.ToUpperInvariant(d[0]) + d[1..], 2);
        }
        if (n is "mouthsmileleft" or "mouthsmileright")
        {
            return (n.EndsWith("left", StringComparison.Ordinal) ? "smileLeft" : "smileRight", 5); // 下で「にっこり」にまとめる
        }
        return null;
    }

    /// <summary>
    /// 表情を推し量る。<paramref name="meshes"/> は glTF のメッシュの番号と、その表情の元の名前（番号の順）。
    /// </summary>
    public static List<VrmExpression> Guess(IReadOnlyList<(int Mesh, IReadOnlyList<string> Targets)> meshes)
    {
        var existing = new HashSet<string>(meshes.SelectMany(m => m.Targets), StringComparer.Ordinal);
        var best = new Dictionary<string, (int Priority, string Name)>(StringComparer.Ordinal);
        foreach (var (_, targets) in meshes)
        {
            foreach (string name in targets)
            {
                if (Classify(name) is { } c && (!best.TryGetValue(c.Key, out var known) || c.Priority < known.Priority))
                {
                    best[c.Key] = (c.Priority, name);
                }
            }
        }
        List<(int, int, float)> Binds(params string[] names) =>
            [.. meshes.SelectMany(m => m.Targets.Select((t, i) => (m.Mesh, i, t)))
                .Where(x => names.Contains(x.t, StringComparer.Ordinal))
                .Select(x => (x.Mesh, x.i, 1f))];

        var result = new List<VrmExpression>();
        void Add(string key, params string[] names)
        {
            // 表情の元がもう MMD の名前なら、そのまま使える（同じ名前の表情を作らない）
            if (VrmExpressions.MmdName(key) is not { } mmd || existing.Contains(mmd.Name))
            {
                return;
            }
            List<(int, int, float)> binds = Binds(names);
            if (binds.Count > 0)
            {
                result.Add(new VrmExpression(key, string.Join("+", names), false, false, binds, [], false, []));
            }
        }
        foreach (string key in new[] { "blink", "blinkLeft", "blinkRight", "aa", "ih", "ou", "ee", "oh", "happy", "relaxed", "angry", "sad", "surprised",
            "lookUp", "lookDown", "lookLeft", "lookRight" })
        {
            if (best.TryGetValue(key, out var found))
            {
                Add(key, found.Name);
            }
            else if (key == "blink" && best.TryGetValue("blinkLeft", out var left) && best.TryGetValue("blinkRight", out var right))
            {
                Add(key, left.Name, right.Name); // 両目を閉じる表情が無ければ、左右のウィンクを合わせる
            }
            else if (key == "relaxed" && best.TryGetValue("smileLeft", out var smileLeft) && best.TryGetValue("smileRight", out var smileRight))
            {
                Add(key, smileLeft.Name, smileRight.Name);
            }
        }
        return result;
    }
}
