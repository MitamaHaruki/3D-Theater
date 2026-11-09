using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

/// <summary>テスト用の PMX の中身。実在のモデルは使わない（権利の確認が要らないよう、すべてここで作る）。</summary>
internal static class TestModels
{
    public static readonly PmxIndexSizes SmallSizes = new(1, 1, 1, 1, 1, 1);
    public static readonly PmxIndexSizes MixedSizes = new(2, 4, 2, 2, 4, 2);

    /// <summary>三角形 1 枚・材質 1 つ・ボーン 1 本の最小のモデル。</summary>
    public static PmxDocument Triangle(PmxIndexSizes? sizes = null, PmxTextEncoding encoding = PmxTextEncoding.Utf16, float version = 2.0f) => new()
    {
        Version = version,
        Encoding = encoding,
        IndexSizes = sizes ?? SmallSizes,
        Name = "三角",
        NameUniversal = "Triangle",
        Comment = "テスト用",
        Vertices =
        [
            Bdef1(new Vector3(0, 0, 0), 0),
            Bdef1(new Vector3(1, 0, 0), 0),
            Bdef1(new Vector3(0, 1, 0), 0),
        ],
        Indices = [0, 1, 2],
        Materials = [new PmxMaterial { Name = "材質", Diffuse = Vector4.One, IndexCount = 3, SharedToon = true, ToonIndex = 0 }],
        Bones = [new PmxBone { Name = "センター", Flags = PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible | PmxBoneFlags.Operable }],
    };

    /// <summary>PMX 2.0 のほぼすべての要素を 1 つ以上含むモデル。</summary>
    public static PmxDocument Full(PmxIndexSizes sizes, PmxTextEncoding encoding, int additionalUvCount = 2) => new()
    {
        Version = 2.0f,
        Encoding = encoding,
        AdditionalUvCount = additionalUvCount,
        IndexSizes = sizes,
        Name = "全部入り",
        NameUniversal = "Full",
        Comment = "コメント\r\n2 行目",
        CommentUniversal = "comment",
        Vertices =
        [
            Bdef1(new Vector3(0, 0, 0), 0),
            new PmxVertex
            {
                Position = new Vector3(1, 0, 0), Normal = Vector3.UnitZ, Uv = new Vector2(1, 0),
                WeightType = PmxWeightType.Bdef2, Bone1 = 0, Bone2 = 1, Bone3 = -1, Bone4 = -1,
                Weights = new Vector4(0.25f, 0.75f, 0, 0), EdgeScale = 0.5f,
            },
            new PmxVertex
            {
                Position = new Vector3(0, 1, 0), Normal = Vector3.UnitZ, Uv = new Vector2(0, 1),
                WeightType = PmxWeightType.Bdef4, Bone1 = 0, Bone2 = 1, Bone3 = 2, Bone4 = -1,
                Weights = new Vector4(0.5f, 0.3f, 0.2f, 0), EdgeScale = 1,
            },
            new PmxVertex
            {
                Position = new Vector3(1, 1, 0), Normal = Vector3.UnitZ, Uv = new Vector2(1, 1),
                WeightType = PmxWeightType.Sdef, Bone1 = 1, Bone2 = 2, Bone3 = -1, Bone4 = -1,
                // 読み込み側は 2 本目の重みを 1 - w で求めるので、2 進数で割り切れる値にする
                Weights = new Vector4(0.625f, 0.375f, 0, 0), SdefC = new Vector3(0.5f, 1, 0),
                SdefR0 = new Vector3(0.1f, 0.2f, 0.3f), SdefR1 = new Vector3(0.4f, 0.5f, 0.6f), EdgeScale = 1,
            },
        ],
        AdditionalUvs = [.. Enumerable.Range(0, 4 * additionalUvCount).Select(i => new Vector4(i, i + 0.5f, -i, 1))],
        Indices = [0, 1, 2, 1, 3, 2],
        Textures = ["tex\\body.png", "sphere.spa", "toon03.bmp"],
        Materials =
        [
            new PmxMaterial
            {
                Name = "体", NameUniversal = "body", Diffuse = new Vector4(1, 0.9f, 0.8f, 1), Specular = new Vector3(0.1f, 0.2f, 0.3f),
                Specularity = 10, Ambient = new Vector3(0.5f, 0.45f, 0.4f),
                Flags = PmxMaterialFlags.DoubleSided | PmxMaterialFlags.Edge | PmxMaterialFlags.CastSelfShadow,
                EdgeColor = new Vector4(0, 0, 0, 1), EdgeSize = 1.5f, TextureIndex = 0, SphereTextureIndex = 1,
                SphereMode = PmxSphereMode.Add, SharedToon = false, ToonIndex = 2, Memo = "メモ", IndexCount = 3,
            },
            new PmxMaterial
            {
                Name = "髪", Diffuse = Vector4.One, TextureIndex = -1, SphereTextureIndex = -1, SharedToon = true, ToonIndex = 4, IndexCount = 3,
            },
        ],
        Bones =
        [
            new PmxBone
            {
                Name = "センター", NameUniversal = "center", Position = new Vector3(0, 8, 0), ParentIndex = -1,
                Flags = PmxBoneFlags.TailIsBone | PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible | PmxBoneFlags.Operable,
                TailBone = 1,
            },
            new PmxBone
            {
                Name = "腕", Position = new Vector3(1, 12, 0), ParentIndex = 0, Layer = 1,
                Flags = PmxBoneFlags.Rotatable | PmxBoneFlags.InheritRotation | PmxBoneFlags.InheritTranslation | PmxBoneFlags.FixedAxis
                    | PmxBoneFlags.LocalAxis | PmxBoneFlags.AfterPhysics,
                TailOffset = new Vector3(0, -1, 0), InheritParent = 0, InheritWeight = 0.5f, FixedAxis = Vector3.UnitX,
                LocalAxisX = Vector3.UnitX, LocalAxisZ = Vector3.UnitZ,
            },
            new PmxBone
            {
                Name = "足ＩＫ", Position = new Vector3(0, 1, 0), ParentIndex = 0, Flags = PmxBoneFlags.IK | PmxBoneFlags.Translatable,
                IkTarget = 1, IkLoopCount = 40, IkLimitAngle = 2f,
                IkLinks =
                [
                    new PmxIkLink { Bone = 1, HasLimits = true, MinAngle = new Vector3(-3.1f, 0, 0), MaxAngle = new Vector3(-0.01f, 0, 0) },
                    new PmxIkLink { Bone = 0 },
                ],
            },
        ],
        Morphs =
        [
            new PmxMorph { Name = "あ", Panel = PmxMorphPanel.Mouth, Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(3, new Vector3(0, 0.1f, 0))] },
            new PmxMorph { Name = "まばたき", Panel = PmxMorphPanel.Eye, Type = PmxMorphType.Bone, BoneOffsets = [new PmxBoneOffset(1, Vector3.UnitY, new Quaternion(0, 0, 0.7071f, 0.7071f))] },
            new PmxMorph { Name = "UV", Panel = PmxMorphPanel.Other, Type = PmxMorphType.Uv, UvOffsets = [new PmxUvOffset(2, new Vector4(0.1f, 0.2f, 0, 0))] },
            new PmxMorph { Name = "追加UV1", Panel = PmxMorphPanel.Other, Type = PmxMorphType.AdditionalUv1, UvOffsets = [new PmxUvOffset(1, Vector4.One)] },
            new PmxMorph
            {
                Name = "照れ", Panel = PmxMorphPanel.Other, Type = PmxMorphType.Material,
                MaterialOffsets = [new PmxMaterialOffset(-1, PmxMaterialOperation.Add, new Vector4(0.1f, 0, 0, 0), Vector3.Zero, 0, Vector3.Zero, Vector4.Zero, 0, Vector4.Zero, Vector4.Zero, Vector4.Zero)],
            },
            new PmxMorph { Name = "まとめ", Panel = PmxMorphPanel.Other, Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(0, 0.5f), new PmxGroupOffset(1, 1f)] },
        ],
        DisplayFrames =
        [
            new PmxDisplayFrame { Name = "Root", NameUniversal = "Root", IsSpecial = true, Elements = [new PmxDisplayElement(PmxDisplayTarget.Bone, 0)] },
            new PmxDisplayFrame { Name = "表情", NameUniversal = "Exp", IsSpecial = true, Elements = [new PmxDisplayElement(PmxDisplayTarget.Morph, 0), new PmxDisplayElement(PmxDisplayTarget.Morph, 5)] },
        ],
        RigidBodies =
        [
            new PmxRigidBody
            {
                Name = "頭", BoneIndex = 0, Group = 3, CollisionMask = 0xFFF7, Shape = PmxRigidShape.Capsule, Size = new Vector3(1, 2, 0),
                Position = new Vector3(0, 15, 0), Rotation = new Vector3(0.1f, 0.2f, 0.3f), Mass = 1, LinearDamping = 0.5f,
                AngularDamping = 0.5f, Restitution = 0.1f, Friction = 0.5f, Mode = PmxPhysicsMode.Physics,
            },
            new PmxRigidBody { Name = "体", BoneIndex = 1, Shape = PmxRigidShape.Box, Size = Vector3.One, Mode = PmxPhysicsMode.FollowBone },
        ],
        Joints =
        [
            new PmxJoint
            {
                Name = "首", Type = PmxJointType.Spring6Dof, RigidBodyA = 1, RigidBodyB = 0, Position = new Vector3(0, 14, 0),
                PositionMin = new Vector3(-1, -1, -1), PositionMax = Vector3.One, RotationMin = new Vector3(-0.5f, -0.5f, -0.5f),
                RotationMax = new Vector3(0.5f, 0.5f, 0.5f), PositionSpring = new Vector3(10, 10, 10), RotationSpring = new Vector3(5, 5, 5),
            },
        ],
    };

    public static PmxVertex Bdef1(Vector3 position, int bone) => new()
    {
        Position = position,
        Normal = Vector3.UnitZ,
        WeightType = PmxWeightType.Bdef1,
        Bone1 = bone,
        Bone2 = -1,
        Bone3 = -1,
        Bone4 = -1,
        Weights = new Vector4(1, 0, 0, 0),
        EdgeScale = 1,
    };
}
