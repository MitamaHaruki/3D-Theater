using System.Numerics;

namespace Theater3D.Formats.Pmx;

// PMX 2.0 / 2.1 の中身をそのまま持つ。名前やメモなど元ファイルの情報は捨てずに残す（指示書 4.1・4.4）。
// 座標は PMX も 3D Theater も左手系・Y が上なので、変換せずに持つ。角度はラジアン。
// 「参照」（ボーン番号など）は PMX のまま。-1 は「なし」。範囲外の値も読み込み時には直さず、レポートで知らせる。

public enum PmxTextEncoding : byte
{
    Utf16 = 0,
    Utf8 = 1,
}

/// <summary>各種の番号を何バイトで書くか（1・2・4）。書き出すときに元と同じにするため残す。</summary>
public readonly record struct PmxIndexSizes(byte Vertex, byte Texture, byte Material, byte Bone, byte Morph, byte RigidBody);

public enum PmxWeightType : byte
{
    Bdef1 = 0,
    Bdef2 = 1,
    Bdef4 = 2,
    Sdef = 3,
    /// <summary>PMX 2.1 で追加。</summary>
    Qdef = 4,
}

public struct PmxVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public PmxWeightType WeightType;
    public int Bone1;
    public int Bone2;
    public int Bone3;
    public int Bone4;
    /// <summary>ボーン 1〜4 の重み。BDEF1 は (1,0,0,0)、BDEF2 と SDEF は (w, 1-w, 0, 0)。</summary>
    public Vector4 Weights;
    public Vector3 SdefC;
    public Vector3 SdefR0;
    public Vector3 SdefR1;
    public float EdgeScale;
}

[Flags]
public enum PmxMaterialFlags : byte
{
    None = 0,
    DoubleSided = 1 << 0,
    GroundShadow = 1 << 1,
    CastSelfShadow = 1 << 2,
    ReceiveSelfShadow = 1 << 3,
    Edge = 1 << 4,
    /// <summary>PMX 2.1。</summary>
    VertexColor = 1 << 5,
    /// <summary>PMX 2.1。</summary>
    PointDraw = 1 << 6,
    /// <summary>PMX 2.1。</summary>
    LineDraw = 1 << 7,
}

public enum PmxSphereMode : byte
{
    None = 0,
    Multiply = 1,
    Add = 2,
    SubTexture = 3,
}

public sealed class PmxMaterial
{
    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    public Vector4 Diffuse { get; init; }
    public Vector3 Specular { get; init; }
    public float Specularity { get; init; }
    public Vector3 Ambient { get; init; }
    public PmxMaterialFlags Flags { get; init; }
    public Vector4 EdgeColor { get; init; }
    public float EdgeSize { get; init; }
    public int TextureIndex { get; init; } = -1;
    public int SphereTextureIndex { get; init; } = -1;
    public PmxSphereMode SphereMode { get; init; }
    /// <summary>true なら <see cref="ToonIndex"/> は MMD 付属の共有トゥーン（0 = toon01.bmp 〜 9 = toon10.bmp）。false ならテクスチャの番号。</summary>
    public bool SharedToon { get; init; }
    public int ToonIndex { get; init; } = -1;
    public string Memo { get; init; } = string.Empty;
    /// <summary>この材質が使う面の頂点番号の数（3 の倍数）。材質は面の並び順に区切って使う。</summary>
    public int IndexCount { get; init; }
}

[Flags]
public enum PmxBoneFlags : ushort
{
    None = 0,
    /// <summary>先端をボーンで指定する（無ければ座標のずれで指定）。</summary>
    TailIsBone = 0x0001,
    Rotatable = 0x0002,
    Translatable = 0x0004,
    Visible = 0x0008,
    Operable = 0x0010,
    IK = 0x0020,
    /// <summary>付与の元を親のローカル変形量にする。</summary>
    InheritLocal = 0x0080,
    InheritRotation = 0x0100,
    InheritTranslation = 0x0200,
    FixedAxis = 0x0400,
    LocalAxis = 0x0800,
    AfterPhysics = 0x1000,
    ExternalParent = 0x2000,
}

public sealed class PmxIkLink
{
    public int Bone { get; init; } = -1;
    public bool HasLimits { get; init; }
    public Vector3 MinAngle { get; init; }
    public Vector3 MaxAngle { get; init; }
}

public sealed class PmxBone
{
    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    public Vector3 Position { get; init; }
    public int ParentIndex { get; init; } = -1;
    /// <summary>変形階層。小さいものから順に計算する。</summary>
    public int Layer { get; init; }
    public PmxBoneFlags Flags { get; init; }
    public int TailBone { get; init; } = -1;
    public Vector3 TailOffset { get; init; }
    public int InheritParent { get; init; } = -1;
    public float InheritWeight { get; init; }
    public Vector3 FixedAxis { get; init; }
    public Vector3 LocalAxisX { get; init; }
    public Vector3 LocalAxisZ { get; init; }
    public int ExternalParentKey { get; init; }
    public int IkTarget { get; init; } = -1;
    public int IkLoopCount { get; init; }
    /// <summary>IK の 1 回の計算で回してよい角度（ラジアン）。</summary>
    public float IkLimitAngle { get; init; }
    public IReadOnlyList<PmxIkLink> IkLinks { get; init; } = [];

    public bool Has(PmxBoneFlags flag) => (Flags & flag) == flag;
}

public enum PmxMorphPanel : byte
{
    System = 0,
    Eyebrow = 1,
    Eye = 2,
    Mouth = 3,
    Other = 4,
}

public enum PmxMorphType : byte
{
    Group = 0,
    Vertex = 1,
    Bone = 2,
    Uv = 3,
    AdditionalUv1 = 4,
    AdditionalUv2 = 5,
    AdditionalUv3 = 6,
    AdditionalUv4 = 7,
    Material = 8,
    /// <summary>PMX 2.1。</summary>
    Flip = 9,
    /// <summary>PMX 2.1。</summary>
    Impulse = 10,
}

public enum PmxMaterialOperation : byte
{
    Multiply = 0,
    Add = 1,
}

/// <summary>グループモーフとフリップモーフの 1 要素。</summary>
public readonly record struct PmxGroupOffset(int Morph, float Weight);

public readonly record struct PmxVertexOffset(int Vertex, Vector3 Translation);

public readonly record struct PmxBoneOffset(int Bone, Vector3 Translation, Quaternion Rotation);

public readonly record struct PmxUvOffset(int Vertex, Vector4 Value);

public sealed record PmxMaterialOffset(
    int Material,
    PmxMaterialOperation Operation,
    Vector4 Diffuse,
    Vector3 Specular,
    float Specularity,
    Vector3 Ambient,
    Vector4 EdgeColor,
    float EdgeSize,
    Vector4 TextureTint,
    Vector4 SphereTint,
    Vector4 ToonTint);

public readonly record struct PmxImpulseOffset(int RigidBody, bool Local, Vector3 Velocity, Vector3 Torque);

/// <summary>モーフ。種類に合った 1 つの一覧だけに中身が入る。</summary>
public sealed class PmxMorph
{
    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    public PmxMorphPanel Panel { get; init; }
    public PmxMorphType Type { get; init; }
    public IReadOnlyList<PmxGroupOffset> GroupOffsets { get; init; } = [];
    public IReadOnlyList<PmxVertexOffset> VertexOffsets { get; init; } = [];
    public IReadOnlyList<PmxBoneOffset> BoneOffsets { get; init; } = [];
    public IReadOnlyList<PmxUvOffset> UvOffsets { get; init; } = [];
    public IReadOnlyList<PmxMaterialOffset> MaterialOffsets { get; init; } = [];
    public IReadOnlyList<PmxImpulseOffset> ImpulseOffsets { get; init; } = [];

    public int OffsetCount => GroupOffsets.Count + VertexOffsets.Count + BoneOffsets.Count + UvOffsets.Count
        + MaterialOffsets.Count + ImpulseOffsets.Count;
}

public enum PmxDisplayTarget : byte
{
    Bone = 0,
    Morph = 1,
}

public readonly record struct PmxDisplayElement(PmxDisplayTarget Target, int Index);

/// <summary>表示枠（MMD のボーン・表情の一覧の分け方）。</summary>
public sealed class PmxDisplayFrame
{
    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    /// <summary>「Root」「表情」など、MMD が特別に扱う枠。</summary>
    public bool IsSpecial { get; init; }
    public IReadOnlyList<PmxDisplayElement> Elements { get; init; } = [];
}

public enum PmxRigidShape : byte
{
    Sphere = 0,
    Box = 1,
    Capsule = 2,
}

public enum PmxPhysicsMode : byte
{
    /// <summary>ボーンに追従する（物理の影響を受けない）。</summary>
    FollowBone = 0,
    Physics = 1,
    /// <summary>物理演算の結果を、位置だけボーンに合わせる。</summary>
    PhysicsWithBone = 2,
}

public sealed class PmxRigidBody
{
    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    public int BoneIndex { get; init; } = -1;
    public byte Group { get; init; }
    /// <summary>
    /// ぶつかる相手のグループ（ビットが立っているグループと衝突する）。PMX の仕様では「非衝突グループフラグ」と呼ぶが、
    /// 実際のファイルには、PMXEditor で「非衝突」に印を付けたグループのビットを 0 にした値が入っている（saba・three.js と同じ扱い）。
    /// </summary>
    public ushort CollisionMask { get; init; }
    public PmxRigidShape Shape { get; init; }
    public Vector3 Size { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Rotation { get; init; }
    public float Mass { get; init; }
    public float LinearDamping { get; init; }
    public float AngularDamping { get; init; }
    public float Restitution { get; init; }
    public float Friction { get; init; }
    public PmxPhysicsMode Mode { get; init; }
}

public enum PmxJointType : byte
{
    Spring6Dof = 0,
    /// <summary>以下は PMX 2.1。</summary>
    SixDof = 1,
    PointToPoint = 2,
    ConeTwist = 3,
    Slider = 4,
    Hinge = 5,
}

public sealed class PmxJoint
{
    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    public PmxJointType Type { get; init; }
    public int RigidBodyA { get; init; } = -1;
    public int RigidBodyB { get; init; } = -1;
    public Vector3 Position { get; init; }
    public Vector3 Rotation { get; init; }
    public Vector3 PositionMin { get; init; }
    public Vector3 PositionMax { get; init; }
    public Vector3 RotationMin { get; init; }
    public Vector3 RotationMax { get; init; }
    public Vector3 PositionSpring { get; init; }
    public Vector3 RotationSpring { get; init; }
}

/// <summary>PMX ファイル 1 つ分。</summary>
public sealed class PmxDocument
{
    public float Version { get; init; } = 2.0f;
    public PmxTextEncoding Encoding { get; init; }
    /// <summary>頂点ごとの追加 UV の数（0〜4）。</summary>
    public int AdditionalUvCount { get; init; }
    public PmxIndexSizes IndexSizes { get; init; }

    public string Name { get; init; } = string.Empty;
    public string NameUniversal { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public string CommentUniversal { get; init; } = string.Empty;

    public PmxVertex[] Vertices { get; init; } = [];
    /// <summary>追加 UV。頂点 i の j 番目は <c>AdditionalUvs[i * AdditionalUvCount + j]</c>。</summary>
    public Vector4[] AdditionalUvs { get; init; } = [];
    /// <summary>面の頂点番号（3 つで 1 枚の三角形）。</summary>
    public int[] Indices { get; init; } = [];
    public string[] Textures { get; init; } = [];

    /// <summary>
    /// ファイルの中に入っているテクスチャの画像（VRM・GLB。鍵は <see cref="Textures"/> の番号）。ここにある番号は、ファイルを探さずにこの中身を使う
    /// （<see cref="Textures"/> の名前は表示とレポートのためだけ）。PMX と .x では空。
    /// </summary>
    public IReadOnlyDictionary<int, byte[]> EmbeddedTextures { get; init; } = new Dictionary<int, byte[]>();

    public PmxMaterial[] Materials { get; init; } = [];
    public PmxBone[] Bones { get; init; } = [];
    public PmxMorph[] Morphs { get; init; } = [];
    public PmxDisplayFrame[] DisplayFrames { get; init; } = [];
    public PmxRigidBody[] RigidBodies { get; init; } = [];
    public PmxJoint[] Joints { get; init; } = [];

    /// <summary>PMX 2.1 のソフトボディの数。中身はまだ読まない（未対応）。</summary>
    public int SoftBodyCount { get; init; }

    public int FaceCount => Indices.Length / 3;

    public string VersionText => Version.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
