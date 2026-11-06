using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Animation.Tests;

/// <summary>テスト用の小さなモデルを組み立てる。</summary>
internal sealed class TestRigBuilder
{
    private readonly List<PmxBone> _bones = [];
    private readonly List<PmxVertex> _vertices = [];
    private readonly List<PmxMorph> _morphs = [];
    private readonly List<PmxMaterial> _materials = [];

    public int Bone(string name, Vector3 position, int parent = -1, PmxBoneFlags flags = PmxBoneFlags.Rotatable | PmxBoneFlags.Visible,
        int layer = 0, int inheritParent = -1, float inheritWeight = 0, Vector3 fixedAxis = default,
        int ikTarget = -1, int ikLoop = 0, float ikLimit = 0, IReadOnlyList<PmxIkLink>? ikLinks = null)
    {
        _bones.Add(new PmxBone
        {
            Name = name,
            Position = position,
            ParentIndex = parent,
            Flags = flags,
            Layer = layer,
            InheritParent = inheritParent,
            InheritWeight = inheritWeight,
            FixedAxis = fixedAxis,
            IkTarget = ikTarget,
            IkLoopCount = ikLoop,
            IkLimitAngle = ikLimit,
            IkLinks = ikLinks ?? [],
        });
        return _bones.Count - 1;
    }

    public int Vertex(Vector3 position, PmxWeightType type = PmxWeightType.Bdef1, int bone1 = 0, int bone2 = -1,
        float weight1 = 1, Vector3 sdefC = default, Vector3 sdefR0 = default, Vector3 sdefR1 = default, Vector3? normal = null)
    {
        _vertices.Add(new PmxVertex
        {
            Position = position,
            Normal = normal ?? Vector3.UnitY,
            WeightType = type,
            Bone1 = bone1,
            Bone2 = bone2,
            Bone3 = -1,
            Bone4 = -1,
            Weights = type == PmxWeightType.Bdef1 ? new Vector4(1, 0, 0, 0) : new Vector4(weight1, 1 - weight1, 0, 0),
            SdefC = sdefC,
            SdefR0 = sdefR0,
            SdefR1 = sdefR1,
        });
        return _vertices.Count - 1;
    }

    public void Vertex(PmxVertex vertex) => _vertices.Add(vertex);

    public int Morph(PmxMorph morph)
    {
        _morphs.Add(morph);
        return _morphs.Count - 1;
    }

    public int Material(Vector4 diffuse)
    {
        _materials.Add(new PmxMaterial { Name = $"材質{_materials.Count}", Diffuse = diffuse, Specular = Vector3.One, Specularity = 5, Ambient = new Vector3(0.5f) });
        return _materials.Count - 1;
    }

    public PmxDocument Document() => new()
    {
        Bones = [.. _bones],
        Vertices = [.. _vertices],
        Morphs = [.. _morphs],
        Materials = [.. _materials],
    };

    public ModelRig Build() => ModelRig.FromPmx(Document());
}

internal static class TestRigs
{
    public const float KneeMax = -0.5f * MathF.PI / 180f;

    /// <summary>
    /// 片脚だけのモデル（MMD と同じく、前は -Z）。
    /// 0 腰 (0,10,0)、1 足 (1,10,0)、2 ひざ (1,5,0)、3 足首 (1,1,0)、4 足ＩＫ (1,1,0)、5 足D (1,10,0)（足の回転を付与で受ける）
    /// </summary>
    public static ModelRig Leg(bool kneeLimit = true)
    {
        var b = new TestRigBuilder();
        int hip = b.Bone("腰", new Vector3(0, 10, 0));
        int leg = b.Bone("足", new Vector3(1, 10, 0), hip);
        int knee = b.Bone("ひざ", new Vector3(1, 5, 0), leg);
        int ankle = b.Bone("足首", new Vector3(1, 1, 0), knee);
        b.Bone("足ＩＫ", new Vector3(1, 1, 0), -1,
            PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible | PmxBoneFlags.IK,
            ikTarget: ankle, ikLoop: 40, ikLimit: 2f,
            ikLinks:
            [
                new PmxIkLink { Bone = knee, HasLimits = kneeLimit, MinAngle = new Vector3(-MathF.PI, 0, 0), MaxAngle = new Vector3(KneeMax, 0, 0) },
                new PmxIkLink { Bone = leg },
            ]);
        b.Bone("足D", new Vector3(1, 10, 0), hip, PmxBoneFlags.Rotatable | PmxBoneFlags.Visible | PmxBoneFlags.InheritRotation,
            layer: 1, inheritParent: leg, inheritWeight: 1);
        b.Vertex(new Vector3(1, 3, 0), bone1: knee);
        return b.Build();
    }
}

internal static class AssertEx
{
    public static void AreClose(Vector3 expected, Vector3 actual, float tolerance = 1e-4f, string? message = null)
    {
        if (Vector3.Distance(expected, actual) > tolerance)
        {
            Assert.Fail($"{message} 期待 {expected}、実際 {actual}（差 {Vector3.Distance(expected, actual)}）");
        }
    }

    /// <summary>同じ回転か（q と -q は同じ回転）。</summary>
    public static void SameRotation(Quaternion expected, Quaternion actual, float tolerance = 1e-4f, string? message = null)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        if (1 - dot > tolerance)
        {
            Assert.Fail($"{message} 期待 {expected}、実際 {actual}");
        }
    }

    public static void Finite(Vector3 v, string? message = null)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
        {
            Assert.Fail($"{message} 数でない値: {v}");
        }
    }
}
