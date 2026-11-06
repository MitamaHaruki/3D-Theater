using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Animation;

public sealed record RigIkLink(int Bone, bool HasLimits, Vector3 MinAngle, Vector3 MaxAngle)
{
    /// <summary>制限が 1 つの軸だけ（ひざなど）なら、その軸の番号（0 = X、1 = Y、2 = Z）。そうでなければ -1。</summary>
    public int SingleAxis { get; } = !HasLimits ? -1 : SingleLimitedAxis(MinAngle, MaxAngle);

    private static int SingleLimitedAxis(Vector3 min, Vector3 max)
    {
        bool x = min.X != 0 || max.X != 0, y = min.Y != 0 || max.Y != 0, z = min.Z != 0 || max.Z != 0;
        return (x, y, z) switch
        {
            (true, false, false) => 0,
            (false, true, false) => 1,
            (false, false, true) => 2,
            _ => -1,
        };
    }
}

/// <summary>計算用に整えたボーン 1 本。範囲外の参照は読み込み時に「なし」へ直してある。</summary>
public sealed class RigBone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public int Parent { get; internal set; } = -1;
    public Vector3 RestPosition { get; init; }
    /// <summary>親からの位置（親が無ければ原点から）。</summary>
    public Vector3 RestOffset { get; internal set; }
    public bool CanRotate { get; init; }
    public bool CanTranslate { get; init; }
    public bool IsVisible { get; init; }
    public bool IsIk { get; internal set; }
    public int InheritSource { get; internal set; } = -1;
    public float InheritWeight { get; init; }
    public bool InheritRotation { get; internal set; }
    public bool InheritTranslation { get; internal set; }
    public bool InheritLocal { get; init; }
    public Vector3? FixedAxis { get; init; }
    public bool AfterPhysics { get; init; }
    public int Layer { get; init; }
    public int TailBone { get; internal set; } = -1;
    public Vector3 TailOffset { get; init; }
    public int IkTarget { get; internal set; } = -1;
    public int IkLoopCount { get; init; }
    public float IkLimitAngle { get; init; }
    public IReadOnlyList<RigIkLink> IkLinks { get; internal set; } = [];
    /// <summary>どれかの IK のリンクになっているか（付与の元になったとき、IK の回転も渡す）。</summary>
    public bool IsIkLink { get; internal set; }
    public bool HasInherit => InheritSource >= 0 && (InheritRotation || InheritTranslation);
    public List<int> Children { get; } = [];
}

/// <summary>頂点 1 つを、どのボーンにどれだけ従わせるか。番号が範囲外のボーンは -1（重み 0）にしてある。</summary>
public struct SkinWeight
{
    public PmxWeightType Type;
    public int Bone0;
    public int Bone1;
    public int Bone2;
    public int Bone3;
    public Vector4 Weights;
    public Vector3 SdefC;
    /// <summary>SDEF の計算のため、読み込み時に求めておく点（(C + R0') / 2 と (C + R1') / 2）。</summary>
    public Vector3 SdefCr0;
    public Vector3 SdefCr1;
}

public readonly record struct MorphVertexOffset(int Vertex, Vector3 Offset);

public readonly record struct MorphUvOffset(int Vertex, Vector2 Offset);

public readonly record struct MorphBoneOffset(int Bone, Vector3 Translation, Quaternion Rotation);

public readonly record struct MorphGroupOffset(int Morph, float Weight);

/// <summary>材質の値（材質モーフで変わる分）。</summary>
public struct MaterialValues
{
    public Vector4 Diffuse;
    public Vector3 Specular;
    public float SpecularPower;
    public Vector3 Ambient;
    public Vector4 TextureMultiply;
    public Vector4 TextureAdd;
}

public sealed record MorphMaterialOffset(int Material, bool Add, MaterialValues Values);

public sealed class RigMorph
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required PmxMorphPanel Panel { get; init; }
    public required PmxMorphType Type { get; init; }
    public MorphVertexOffset[] VertexOffsets { get; init; } = [];
    public MorphUvOffset[] UvOffsets { get; init; } = [];
    public MorphBoneOffset[] BoneOffsets { get; init; } = [];
    public MorphGroupOffset[] GroupOffsets { get; init; } = [];
    public MorphMaterialOffset[] MaterialOffsets { get; init; } = [];
}

/// <summary>
/// 変形の計算に使う形にしたモデル（ボーン・モーフ・頂点の重み）。元の <see cref="PmxDocument"/> は書き換えない。
/// 壊れた参照（範囲外のボーン番号・親子の輪など）は、計算が止まらないよう「なし」として扱う。
/// </summary>
public sealed class ModelRig
{
    private ModelRig() { }

    public RigBone[] Bones { get; private init; } = [];

    /// <summary>付与と IK を解く順番（物理後のものを最後に、次に変形階層、次に番号）。</summary>
    public int[] DeformOrder { get; private init; } = [];

    /// <summary>親が子より先に来る順番。</summary>
    public int[] HierarchyOrder { get; private init; } = [];

    public RigMorph[] Morphs { get; private init; } = [];

    public SkinWeight[] Skin { get; private init; } = [];

    public Vector3[] RestPositions { get; private init; } = [];

    public Vector3[] RestNormals { get; private init; } = [];

    public Vector2[] RestUvs { get; private init; } = [];

    public MaterialValues[] BaseMaterials { get; private init; } = [];

    public static ModelRig FromPmx(PmxDocument d)
    {
        int boneCount = d.Bones.Length;
        bool ValidBone(int index) => (uint)index < (uint)boneCount;

        var bones = new RigBone[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            PmxBone b = d.Bones[i];
            bool inheritRotation = b.Has(PmxBoneFlags.InheritRotation);
            bool inheritTranslation = b.Has(PmxBoneFlags.InheritTranslation);
            bool inheritValid = ValidBone(b.InheritParent) && b.InheritParent != i;
            bones[i] = new RigBone
            {
                Index = i,
                Name = b.Name,
                Parent = ValidBone(b.ParentIndex) && b.ParentIndex != i ? b.ParentIndex : -1,
                RestPosition = b.Position,
                CanRotate = b.Has(PmxBoneFlags.Rotatable),
                CanTranslate = b.Has(PmxBoneFlags.Translatable),
                IsVisible = b.Has(PmxBoneFlags.Visible),
                InheritSource = inheritValid && (inheritRotation || inheritTranslation) ? b.InheritParent : -1,
                InheritWeight = b.InheritWeight,
                InheritRotation = inheritValid && inheritRotation,
                InheritTranslation = inheritValid && inheritTranslation,
                InheritLocal = b.Has(PmxBoneFlags.InheritLocal),
                FixedAxis = b.Has(PmxBoneFlags.FixedAxis) && b.FixedAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(b.FixedAxis) : null,
                AfterPhysics = b.Has(PmxBoneFlags.AfterPhysics),
                Layer = b.Layer,
                TailBone = b.Has(PmxBoneFlags.TailIsBone) && ValidBone(b.TailBone) ? b.TailBone : -1,
                TailOffset = b.Has(PmxBoneFlags.TailIsBone) ? Vector3.Zero : b.TailOffset,
                IkLoopCount = Math.Clamp(b.IkLoopCount, 0, 1000),
                IkLimitAngle = b.IkLimitAngle > 0 ? b.IkLimitAngle : MathF.PI,
            };
            if (b.Has(PmxBoneFlags.IK) && ValidBone(b.IkTarget))
            {
                RigIkLink[] links = [.. b.IkLinks.Where(l => ValidBone(l.Bone))
                    .Select(l => new RigIkLink(l.Bone, l.HasLimits, Vector3.Min(l.MinAngle, l.MaxAngle), Vector3.Max(l.MinAngle, l.MaxAngle)))];
                if (links.Length > 0)
                {
                    bones[i].IsIk = true;
                    bones[i].IkTarget = b.IkTarget;
                    bones[i].IkLinks = links;
                }
            }
        }

        // 親子の輪を断つ（輪の中のボーンは親なしにする）
        for (int i = 0; i < boneCount; i++)
        {
            int current = bones[i].Parent;
            for (int steps = 0; current >= 0 && steps <= boneCount; steps++)
            {
                if (current == i)
                {
                    bones[i].Parent = -1;
                    break;
                }
                current = bones[current].Parent;
            }
        }
        foreach (RigBone bone in bones)
        {
            bone.RestOffset = bone.Parent >= 0 ? bone.RestPosition - bones[bone.Parent].RestPosition : bone.RestPosition;
            if (bone.Parent >= 0)
            {
                bones[bone.Parent].Children.Add(bone.Index);
            }
            foreach (RigIkLink link in bone.IkLinks)
            {
                bones[link.Bone].IsIkLink = true;
            }
        }

        var hierarchy = new List<int>(boneCount);
        foreach (RigBone root in bones.Where(b => b.Parent < 0))
        {
            AddSubtree(root.Index);
        }
        void AddSubtree(int index)
        {
            hierarchy.Add(index);
            foreach (int child in bones[index].Children)
            {
                AddSubtree(child);
            }
        }

        int[] deformOrder = [.. bones.OrderBy(b => b.AfterPhysics ? 1 : 0).ThenBy(b => b.Layer).ThenBy(b => b.Index).Select(b => b.Index)];

        return new ModelRig
        {
            Bones = bones,
            HierarchyOrder = [.. hierarchy],
            DeformOrder = deformOrder,
            Morphs = BuildMorphs(d),
            Skin = BuildSkin(d),
            RestPositions = [.. d.Vertices.Select(v => v.Position)],
            RestNormals = [.. d.Vertices.Select(v => v.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(v.Normal) : Vector3.UnitY)],
            RestUvs = [.. d.Vertices.Select(v => v.Uv)],
            BaseMaterials = [.. d.Materials.Select(m => new MaterialValues
            {
                Diffuse = m.Diffuse,
                Specular = m.Specular,
                SpecularPower = m.Specularity,
                Ambient = m.Ambient,
                TextureMultiply = Vector4.One,
                TextureAdd = Vector4.Zero,
            })],
        };
    }

    public int FindBone(string name) => Array.FindIndex(Bones, b => b.Name == name);

    public int FindMorph(string name) => Array.FindIndex(Morphs, m => m.Name == name);

    private static SkinWeight[] BuildSkin(PmxDocument d)
    {
        int boneCount = d.Bones.Length;
        var skin = new SkinWeight[d.Vertices.Length];
        for (int i = 0; i < skin.Length; i++)
        {
            ref readonly PmxVertex v = ref d.Vertices[i];
            int b0 = Valid(v.Bone1), b1 = Valid(v.Bone2), b2 = Valid(v.Bone3), b3 = Valid(v.Bone4);
            var w = new Vector4(b0 >= 0 ? v.Weights.X : 0, b1 >= 0 ? v.Weights.Y : 0, b2 >= 0 ? v.Weights.Z : 0, b3 >= 0 ? v.Weights.W : 0);
            PmxWeightType type = v.WeightType;
            if (type == PmxWeightType.Bdef1)
            {
                w = new Vector4(b0 >= 0 ? 1 : 0, 0, 0, 0);
            }
            else if (type is PmxWeightType.Bdef4 or PmxWeightType.Qdef)
            {
                // 重みの合計が 1 でないモデルもあるので、合計で割って 1 にそろえる
                float sum = w.X + w.Y + w.Z + w.W;
                w = sum > 1e-6f ? w / sum : w;
            }
            else if (type == PmxWeightType.Sdef && (b0 < 0 || b1 < 0))
            {
                type = PmxWeightType.Bdef2; // ボーンが欠けた SDEF は普通の 2 本の重みとして扱う
            }

            var s = new SkinWeight { Type = type, Bone0 = b0, Bone1 = b1, Bone2 = b2, Bone3 = b3, Weights = w };
            if (type == PmxWeightType.Sdef)
            {
                // SDEF: R0・R1 を重みで寄せた点を引いておき、C との中点を求めておく
                Vector3 rw = (v.SdefR0 * w.X) + (v.SdefR1 * w.Y);
                Vector3 r0 = v.SdefC + v.SdefR0 - rw;
                Vector3 r1 = v.SdefC + v.SdefR1 - rw;
                s.SdefC = v.SdefC;
                s.SdefCr0 = (v.SdefC + r0) * 0.5f;
                s.SdefCr1 = (v.SdefC + r1) * 0.5f;
            }
            skin[i] = s;
        }
        return skin;

        int Valid(int bone) => (uint)bone < (uint)boneCount ? bone : -1;
    }

    private static RigMorph[] BuildMorphs(PmxDocument d)
    {
        int vertexCount = d.Vertices.Length, boneCount = d.Bones.Length, morphCount = d.Morphs.Length, materialCount = d.Materials.Length;
        var morphs = new RigMorph[morphCount];
        for (int i = 0; i < morphCount; i++)
        {
            PmxMorph m = d.Morphs[i];
            morphs[i] = new RigMorph
            {
                Index = i,
                Name = m.Name,
                Panel = m.Panel,
                Type = m.Type,
                VertexOffsets = [.. m.VertexOffsets.Where(o => (uint)o.Vertex < (uint)vertexCount).Select(o => new MorphVertexOffset(o.Vertex, o.Translation))],
                UvOffsets = m.Type == PmxMorphType.Uv
                    ? [.. m.UvOffsets.Where(o => (uint)o.Vertex < (uint)vertexCount).Select(o => new MorphUvOffset(o.Vertex, new Vector2(o.Value.X, o.Value.Y)))]
                    : [],
                BoneOffsets = [.. m.BoneOffsets.Where(o => (uint)o.Bone < (uint)boneCount)
                    .Select(o => new MorphBoneOffset(o.Bone, o.Translation, o.Rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(o.Rotation) : Quaternion.Identity))],
                // グループモーフの中のグループモーフと自分自身は、MMD と同じく効かせない
                GroupOffsets = m.Type == PmxMorphType.Group
                    ? [.. m.GroupOffsets.Where(o => (uint)o.Morph < (uint)morphCount && o.Morph != i && d.Morphs[o.Morph].Type != PmxMorphType.Group)
                        .Select(o => new MorphGroupOffset(o.Morph, o.Weight))]
                    : [],
                MaterialOffsets = [.. m.MaterialOffsets.Where(o => o.Material == -1 || (uint)o.Material < (uint)materialCount)
                    .Select(o => new MorphMaterialOffset(o.Material, o.Operation == PmxMaterialOperation.Add, new MaterialValues
                    {
                        Diffuse = o.Diffuse,
                        Specular = o.Specular,
                        SpecularPower = o.Specularity,
                        Ambient = o.Ambient,
                        TextureMultiply = o.TextureTint,
                        TextureAdd = o.TextureTint,
                    }))],
            };
        }
        return morphs;
    }
}
