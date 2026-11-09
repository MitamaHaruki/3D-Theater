using Theater3D.Animation;
using Theater3D.Domain;
using Theater3D.Formats.Pmx;

namespace Theater3D.App.Import;

/// <summary>
/// 読み込んだモデルのボーンとモーフを、シーン一覧の項目にする。
/// ボーンは PMX の表示枠（MMD のボーン一覧の分け方）ごとに、モーフは種類（目・まゆ・口・その他）ごとにまとめる。
/// まとまりは数が多いので、一覧では最初は閉じておく。モデルのファイルの親子関係や順番は変えない（指示書 2.7）。
/// </summary>
internal static class ModelPartsBuilder
{
    public static void Attach(ModelNode model, PmxDocument d, ModelRig rig)
    {
        var bones = new BoneNode[rig.Bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            RigBone b = rig.Bones[i];
            var traits = new BoneTraits(
                b.CanRotate,
                b.CanTranslate,
                b.IsIk,
                b.IsIkLink,
                b.HasInherit,
                b.FixedAxis is not null,
                b.AfterPhysics,
                b.IsVisible,
                b.Parent >= 0 ? NameOrNumber(rig.Bones[b.Parent].Name, "ボーン", b.Parent) : string.Empty);
            bones[i] = new BoneNode(NameOrNumber(b.Name, "ボーン", i), i, traits);
        }

        var morphs = new MorphNode[rig.Morphs.Length];
        for (int i = 0; i < morphs.Length; i++)
        {
            PmxMorph m = d.Morphs[i];
            morphs[i] = new MorphNode(NameOrNumber(m.Name, "モーフ", i), i, Category(m.Panel), TypeName(m.Type)) { CanToggleVisibility = false };
        }

        var groups = new List<SceneNode>();
        if (bones.Length > 0)
        {
            groups.Add(BuildBoneGroups(d, bones));
        }
        if (morphs.Length > 0)
        {
            var morphGroup = Group("表情", canHide: false);
            foreach (var (category, name) in CategoryOrder)
            {
                MorphNode[] members = [.. morphs.Where(m => m.Category == category)];
                if (members.Length == 0)
                {
                    continue;
                }
                var group = Group(name, canHide: false);
                foreach (MorphNode morph in members)
                {
                    group.AddChild(morph);
                }
                morphGroup.AddChild(group);
            }
            groups.Add(morphGroup);
        }
        model.AttachParts(groups, bones, morphs);
    }

    private static readonly (MorphCategory Category, string Name)[] CategoryOrder =
    [
        (MorphCategory.Eye, "目"),
        (MorphCategory.Eyebrow, "まゆ"),
        (MorphCategory.Mouth, "口"),
        (MorphCategory.Other, "その他"),
        (MorphCategory.System, "システム"),
    ];

    public static string CategoryName(MorphCategory category) => CategoryOrder.First(c => c.Category == category).Name;

    private static SceneNode BuildBoneGroups(PmxDocument d, BoneNode[] bones)
    {
        var root = Group("ボーン", canHide: true);
        var placed = new bool[bones.Length];
        int frameNumber = 0;
        foreach (PmxDisplayFrame frame in d.DisplayFrames)
        {
            frameNumber++;
            // 1 本のボーンが複数の枠にあるときは、最初の枠に置く
            int[] members = [.. frame.Elements
                .Where(e => e.Target == PmxDisplayTarget.Bone && (uint)e.Index < (uint)bones.Length && !placed[e.Index])
                .Select(e => e.Index)
                .Distinct()];
            if (members.Length == 0)
            {
                continue;
            }
            var group = Group(NameOrNumber(frame.Name, "表示枠", frameNumber - 1), canHide: true);
            foreach (int index in members)
            {
                placed[index] = true;
                group.AddChild(bones[index]);
            }
            root.AddChild(group);
        }

        BoneNode[] rest = [.. bones.Where(b => !placed[b.Index])];
        if (rest.Length > 0)
        {
            // 表示枠が 1 つも無いモデルはそのまま並べ、枠があるモデルでは残りを 1 つにまとめる
            SceneNode target = root.Children.Count == 0 ? root : Group("表示枠にないボーン", canHide: true);
            foreach (BoneNode bone in rest)
            {
                target.AddChild(bone);
            }
            if (target != root)
            {
                root.AddChild(target);
            }
        }
        return root;
    }

    private static SceneNode Group(string name, bool canHide) =>
        new(SceneNodeKind.Group, name, canRename: false, hasTransform: false) { StartsCollapsed = true, CanToggleVisibility = canHide };

    private static string NameOrNumber(string name, string kind, int index) =>
        string.IsNullOrWhiteSpace(name) ? $"（名前なしの{kind} {index}）" : name.Trim();

    private static MorphCategory Category(PmxMorphPanel panel) => panel switch
    {
        PmxMorphPanel.Eyebrow => MorphCategory.Eyebrow,
        PmxMorphPanel.Eye => MorphCategory.Eye,
        PmxMorphPanel.Mouth => MorphCategory.Mouth,
        PmxMorphPanel.System => MorphCategory.System,
        _ => MorphCategory.Other,
    };

    private static string TypeName(PmxMorphType type) => type switch
    {
        PmxMorphType.Group => "グループ",
        PmxMorphType.Vertex => "頂点",
        PmxMorphType.Bone => "ボーン",
        PmxMorphType.Uv => "UV",
        PmxMorphType.AdditionalUv1 or PmxMorphType.AdditionalUv2 or PmxMorphType.AdditionalUv3 or PmxMorphType.AdditionalUv4
            => $"追加 UV {(int)type - (int)PmxMorphType.AdditionalUv1 + 1}（今は効きません）",
        PmxMorphType.Material => "材質",
        PmxMorphType.Flip => "フリップ",
        PmxMorphType.Impulse => "インパルス（物理演算が ON のとき剛体を押す）",
        _ => "不明",
    };
}
