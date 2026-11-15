using System.Numerics;
using System.Text.Json;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Gltf;

/// <summary>VRM の表情 1 つ（形の変化・材質の色・テクスチャの位置をずらす、の組み合わせ）。</summary>
/// <param name="Key">決まった表情の名前（1.0 の名前にそろえる。aa・blink・happy など）。自分で作った表情は null。</param>
/// <param name="Name">VRM の中の名前。</param>
/// <param name="MorphBinds">形の変化（glTF のメッシュの番号・その中の表情の元の番号・強さ 0〜1）。</param>
/// <param name="ColorBinds">材質の色（glTF の材質の番号・色の種類・目標の色。<paramref name="ColorIsLinear"/> なら光の強さの値）。</param>
/// <param name="UvBinds">テクスチャの位置をずらす（glTF の材質の番号・大きさ・ずらす量。uv' = uv × 大きさ + ずらす量）。</param>
internal sealed record VrmExpression(
    string? Key,
    string Name,
    bool IsBinary,
    bool Overrides,
    IReadOnlyList<(int Mesh, int Target, float Weight)> MorphBinds,
    IReadOnlyList<(int Material, string Type, Vector4 Color)> ColorBinds,
    bool ColorIsLinear,
    IReadOnlyList<(int Material, Vector2 Scale, Vector2 Offset)> UvBinds);

/// <summary>
/// VRM の表情（1.0 の expressions・0.x の blendShapeMaster）を読み、決まった表情を MMD の表情の名前にする（設計判断記録 82 章の 3 段目）。
/// 顔のトラッキングは、まばたき・ウィンク・ウィンク右・あ・い・う・え・お・にっこり・怒り の名前で表情を探す。
/// </summary>
internal static class VrmExpressions
{
    /// <summary>決まった表情 → MMD の表情の名前と、表情の欄の分け方（並びもこの順）。</summary>
    private static readonly (string Key, string Name, PmxMorphPanel Panel)[] Presets =
    [
        ("blink", "まばたき", PmxMorphPanel.Eye),
        ("blinkLeft", "ウィンク", PmxMorphPanel.Eye),
        ("blinkRight", "ウィンク右", PmxMorphPanel.Eye),
        ("aa", "あ", PmxMorphPanel.Mouth),
        ("ih", "い", PmxMorphPanel.Mouth),
        ("ou", "う", PmxMorphPanel.Mouth),
        ("ee", "え", PmxMorphPanel.Mouth),
        ("oh", "お", PmxMorphPanel.Mouth),
        ("happy", "笑い", PmxMorphPanel.Eye),
        ("relaxed", "にっこり", PmxMorphPanel.Mouth),
        ("angry", "怒り", PmxMorphPanel.Eyebrow),
        ("sad", "困る", PmxMorphPanel.Eyebrow),
        ("surprised", "びっくり", PmxMorphPanel.Eye),
        ("lookUp", "視線上", PmxMorphPanel.Eye),
        ("lookDown", "視線下", PmxMorphPanel.Eye),
        ("lookLeft", "視線左", PmxMorphPanel.Eye),
        ("lookRight", "視線右", PmxMorphPanel.Eye),
        ("neutral", "ニュートラル", PmxMorphPanel.Other),
    ];

    // VRM 0.x の presetName → 1.0 の名前（0.x の joy・sorrow・fun は、1.0 の happy・sad・relaxed）
    private static readonly Dictionary<string, string> Vrm0Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "aa",
        ["i"] = "ih",
        ["u"] = "ou",
        ["e"] = "ee",
        ["o"] = "oh",
        ["blink"] = "blink",
        ["blink_l"] = "blinkLeft",
        ["blink_r"] = "blinkRight",
        ["joy"] = "happy",
        ["angry"] = "angry",
        ["sorrow"] = "sad",
        ["fun"] = "relaxed",
        ["lookup"] = "lookUp",
        ["lookdown"] = "lookDown",
        ["lookleft"] = "lookLeft",
        ["lookright"] = "lookRight",
        ["neutral"] = "neutral",
    };

    /// <summary>決まった表情の MMD の名前と欄（決まった表情でなければ null）。</summary>
    public static (string Name, PmxMorphPanel Panel)? MmdName(string? key) =>
        key is not null && Array.FindIndex(Presets, p => p.Key == key) is int i and >= 0 ? (Presets[i].Name, Presets[i].Panel) : null;

    /// <summary>決まった表情の並び（MMD の表情の欄に並べる順）。</summary>
    public static int Order(string? key) => key is null ? int.MaxValue : Array.FindIndex(Presets, p => p.Key == key) is int i and >= 0 ? i : int.MaxValue;

    /// <summary>
    /// VRM の表情を読む。<paramref name="meshOfNode"/> はノードの番号 → メッシュの番号（1.0 はノードで指す）、
    /// <paramref name="materialByName"/> は材質の名前 → 番号（0.x は名前で指す）。目線を表情で動かすモデルなら <c>LookAtByExpression</c>。
    /// </summary>
    public static (List<VrmExpression> Expressions, bool LookAtByExpression) Read(JsonElement root, VrmVersion version, Func<int, int> meshOfNode,
        Func<string, int> materialByName)
    {
        var result = new List<VrmExpression>();
        if (!GltfFile.TryGet(root, "extensions", out JsonElement extensions))
        {
            return (result, false);
        }
        if (version == VrmVersion.Vrm1 && GltfFile.TryGet(extensions, "VRMC_vrm", out JsonElement vrm))
        {
            if (GltfFile.TryGet(vrm, "expressions", out JsonElement expressions))
            {
                foreach (string kind in new[] { "preset", "custom" })
                {
                    if (!GltfFile.TryGet(expressions, kind, out JsonElement group) || group.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    foreach (JsonProperty property in group.EnumerateObject())
                    {
                        result.Add(ReadVrm1(property.Name, kind == "preset" ? property.Name : null, property.Value, meshOfNode));
                    }
                }
            }
            bool byExpression = GltfFile.TryGet(vrm, "lookAt", out JsonElement lookAt) && GltfFile.String(lookAt, "type") == "expression";
            return (result, byExpression);
        }
        if (version == VrmVersion.Vrm0 && GltfFile.TryGet(extensions, "VRM", out JsonElement vrm0))
        {
            if (GltfFile.TryGet(vrm0, "blendShapeMaster", out JsonElement master) && GltfFile.TryGet(master, "blendShapeGroups", out JsonElement groups)
                && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement group in groups.EnumerateArray())
                {
                    result.Add(ReadVrm0(group, materialByName));
                }
            }
            bool byExpression = GltfFile.TryGet(vrm0, "firstPerson", out JsonElement firstPerson)
                && string.Equals(GltfFile.String(firstPerson, "lookAtTypeName"), "BlendShape", StringComparison.OrdinalIgnoreCase);
            return (result, byExpression);
        }
        return (result, false);
    }

    private static VrmExpression ReadVrm1(string name, string? key, JsonElement e, Func<int, int> meshOfNode)
    {
        var morphs = new List<(int, int, float)>();
        foreach (JsonElement bind in Items(e, "morphTargetBinds"))
        {
            int mesh = meshOfNode(GltfFile.Int(bind, "node", -1));
            int index = GltfFile.Int(bind, "index", -1);
            if (mesh >= 0 && index >= 0)
            {
                morphs.Add((mesh, index, GltfFile.Float(bind, "weight", 1f)));
            }
        }
        var colors = new List<(int, string, Vector4)>();
        foreach (JsonElement bind in Items(e, "materialColorBinds"))
        {
            float[]? value = GltfFile.Floats(bind, "targetValue", 4);
            int material = GltfFile.Int(bind, "material", -1);
            if (value is not null && material >= 0)
            {
                colors.Add((material, GltfFile.String(bind, "type") ?? string.Empty, new Vector4(value[0], value[1], value[2], value[3])));
            }
        }
        var uvs = new List<(int, Vector2, Vector2)>();
        foreach (JsonElement bind in Items(e, "textureTransformBinds"))
        {
            int material = GltfFile.Int(bind, "material", -1);
            float[] scale = GltfFile.Floats(bind, "scale", 2) ?? [1, 1];
            float[] offset = GltfFile.Floats(bind, "offset", 2) ?? [0, 0];
            if (material >= 0)
            {
                uvs.Add((material, new Vector2(scale[0], scale[1]), new Vector2(offset[0], offset[1])));
            }
        }
        bool binary = GltfFile.TryGet(e, "isBinary", out JsonElement b) && b.ValueKind == JsonValueKind.True;
        bool overrides = new[] { "overrideBlink", "overrideLookAt", "overrideMouth" }
            .Any(o => GltfFile.String(e, o) is { } value && value != "none");
        return new VrmExpression(key, name, binary, overrides, morphs, colors, ColorIsLinear: true, uvs);
    }

    private static VrmExpression ReadVrm0(JsonElement group, Func<string, int> materialByName)
    {
        string preset = GltfFile.String(group, "presetName") ?? string.Empty;
        string? key = Vrm0Presets.TryGetValue(preset, out string? k) ? k : null;
        string name = GltfFile.String(group, "name") is { Length: > 0 } n ? n : preset;
        var morphs = new List<(int, int, float)>();
        foreach (JsonElement bind in Items(group, "binds"))
        {
            int mesh = GltfFile.Int(bind, "mesh", -1);
            int index = GltfFile.Int(bind, "index", -1);
            if (mesh >= 0 && index >= 0)
            {
                morphs.Add((mesh, index, GltfFile.Float(bind, "weight", 100f) / 100f)); // 0.x の強さは 0〜100
            }
        }
        var colors = new List<(int, string, Vector4)>();
        var uvs = new List<(int, Vector2, Vector2)>();
        foreach (JsonElement value in Items(group, "materialValues"))
        {
            int material = materialByName(GltfFile.String(value, "materialName") ?? string.Empty);
            float[]? target = GltfFile.Floats(value, "targetValue", 4);
            if (material < 0 || target is null)
            {
                continue;
            }
            switch (GltfFile.String(value, "propertyName"))
            {
                case "_Color":
                    colors.Add((material, "color", new Vector4(target[0], target[1], target[2], target[3])));
                    break;
                case "_MainTex_ST":
                    // Unity の決まり（V が上向き）の大きさ・ずらす量を、glTF（V が下向き）に直す: v' = v × sy + (1 - sy - oy)
                    uvs.Add((material, new Vector2(target[0], target[1]), new Vector2(target[2], 1f - target[1] - target[3])));
                    break;
                default:
                    colors.Add((material, GltfFile.String(value, "propertyName") ?? string.Empty, new Vector4(target[0], target[1], target[2], target[3])));
                    break;
            }
        }
        bool binary = GltfFile.TryGet(group, "isBinary", out JsonElement b) && b.ValueKind == JsonValueKind.True;
        return new VrmExpression(key, name, binary, Overrides: false, morphs, colors, ColorIsLinear: false, uvs);
    }

    private static IEnumerable<JsonElement> Items(JsonElement owner, string name) =>
        GltfFile.TryGet(owner, name, out JsonElement array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
}
