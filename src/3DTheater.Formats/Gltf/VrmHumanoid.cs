using System.Text.Json;

namespace Theater3D.Formats.Gltf;

/// <summary>
/// VRM の人型のボーン（humanoid。hips・spine・head・leftUpperArm など）と、MMD の標準のボーンの名前の対応（設計判断記録 82 章の 2 段目）。
/// 親指は VRM の版で名前の付け方が違う（0.x は Proximal・Intermediate・Distal、1.0 は Metacarpal・Proximal・Distal）ので、どちらも
/// 根元から 親指０・親指１・親指２ にする。つま先（toes）は、MMD の準標準の「足先EX」にする（MMD の「つま先」は IK の目印の先端で、別に足す）。
/// </summary>
internal static class VrmHumanoid
{
    private static readonly Dictionary<string, string> Body = new(StringComparer.Ordinal)
    {
        ["hips"] = "下半身",
        ["spine"] = "上半身",
        ["chest"] = "上半身2",
        ["upperChest"] = "上半身3",
        ["neck"] = "首",
        ["head"] = "頭",
    };

    /// <summary>VRM の人型のボーンの名前 → ノードの番号。人型の決まりが無ければ空。</summary>
    public static Dictionary<string, int> Read(JsonElement root, VrmVersion version, int nodeCount)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        void Add(string? bone, int node)
        {
            // 同じノードを 2 つの名前で指していれば、先の名前だけ使う
            if (!string.IsNullOrEmpty(bone) && (uint)node < (uint)nodeCount && !result.ContainsKey(bone) && taken.Add(node))
            {
                result[bone] = node;
            }
        }
        if (!GltfFile.TryGet(root, "extensions", out JsonElement extensions))
        {
            return result;
        }
        if (version == VrmVersion.Vrm1 && GltfFile.TryGet(extensions, "VRMC_vrm", out JsonElement vrm1)
            && GltfFile.TryGet(vrm1, "humanoid", out JsonElement humanoid1) && GltfFile.TryGet(humanoid1, "humanBones", out JsonElement bones1)
            && bones1.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty bone in bones1.EnumerateObject())
            {
                Add(bone.Name, GltfFile.Int(bone.Value, "node", -1));
            }
        }
        else if (version == VrmVersion.Vrm0 && GltfFile.TryGet(extensions, "VRM", out JsonElement vrm0)
            && GltfFile.TryGet(vrm0, "humanoid", out JsonElement humanoid0) && GltfFile.TryGet(humanoid0, "humanBones", out JsonElement bones0)
            && bones0.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement bone in bones0.EnumerateArray())
            {
                Add(GltfFile.String(bone, "bone"), GltfFile.Int(bone, "node", -1));
            }
        }
        return result;
    }

    /// <summary>VRM の人型のボーンの名前に対応する、MMD の標準のボーンの名前（対応が無ければ null。あご・目以外の顔など）。</summary>
    public static string? MmdName(string vrmBone, VrmVersion version)
    {
        if (Body.TryGetValue(vrmBone, out string? body))
        {
            return body;
        }
        string side;
        string part;
        if (vrmBone.StartsWith("left", StringComparison.Ordinal))
        {
            side = "左";
            part = vrmBone[4..];
        }
        else if (vrmBone.StartsWith("right", StringComparison.Ordinal))
        {
            side = "右";
            part = vrmBone[5..];
        }
        else
        {
            return null;
        }
        string? name = part switch
        {
            "Eye" => "目",
            "Shoulder" => "肩",
            "UpperArm" => "腕",
            "LowerArm" => "ひじ",
            "Hand" => "手首",
            "UpperLeg" => "足",
            "LowerLeg" => "ひざ",
            "Foot" => "足首",
            "Toes" => "足先EX",
            "ThumbMetacarpal" => "親指０",
            "ThumbProximal" => version == VrmVersion.Vrm0 ? "親指０" : "親指１",
            "ThumbIntermediate" => "親指１",
            "ThumbDistal" => "親指２",
            "IndexProximal" => "人指１",
            "IndexIntermediate" => "人指２",
            "IndexDistal" => "人指３",
            "MiddleProximal" => "中指１",
            "MiddleIntermediate" => "中指２",
            "MiddleDistal" => "中指３",
            "RingProximal" => "薬指１",
            "RingIntermediate" => "薬指２",
            "RingDistal" => "薬指３",
            "LittleProximal" => "小指１",
            "LittleIntermediate" => "小指２",
            "LittleDistal" => "小指３",
            _ => null,
        };
        return name is null ? null : side + name;
    }
}
