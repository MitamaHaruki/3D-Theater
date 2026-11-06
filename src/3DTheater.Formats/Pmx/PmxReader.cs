using System.IO;
using System.Text;

namespace Theater3D.Formats.Pmx;

/// <summary>読み込みの結果。失敗したときは <see cref="Document"/> が null で、理由は <see cref="Report"/> にある。</summary>
public sealed record PmxLoadResult(PmxDocument? Document, LoadReport Report, string? SourcePath)
{
    public bool Succeeded => Document is not null;
}

/// <summary>
/// PMX 2.0 / 2.1 を読む（指示書 4.1）。元のファイルは読むだけで書き換えない。
/// 形式の誤りで読めないときは例外ではなく、失敗した結果（エラー入りのレポート）を返す。
/// 読めた後に、範囲外の参照や見つからない素材、未対応の要素を調べてレポートに載せる（<see cref="PmxValidator"/>）。
/// </summary>
public static class PmxReader
{
    private const float VersionTolerance = 0.001f;

    public static PmxLoadResult Load(string path)
    {
        var report = new LoadReport();
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            report.Error(LoadIssueCategory.Format, $"ファイルを開けませんでした: {ex.Message}");
            return new PmxLoadResult(null, report, path);
        }
        return Read(data, Path.GetDirectoryName(Path.GetFullPath(path)), path, report);
    }

    /// <summary>メモリ上のデータから読む。<paramref name="baseDirectory"/> があればテクスチャの有無も調べる。</summary>
    public static PmxLoadResult Read(byte[] data, string? baseDirectory = null) => Read(data, baseDirectory, null, new LoadReport());

    private static PmxLoadResult Read(byte[] data, string? baseDirectory, string? path, LoadReport report)
    {
        PmxDocument document;
        try
        {
            document = Parse(data, report);
        }
        catch (ModelFormatException ex)
        {
            report.Error(LoadIssueCategory.Format, ex.Message);
            return new PmxLoadResult(null, report, path);
        }
        PmxValidator.Validate(document, report, baseDirectory);
        return new PmxLoadResult(document, report, path);
    }

    private static PmxDocument Parse(byte[] data, LoadReport report)
    {
        var c = new BinaryCursor(data);

        // ヘッダー
        ReadOnlySpan<byte> signature = c.Take(4);
        if (signature[..3].SequenceEqual("Pmd"u8))
        {
            throw new ModelFormatException("PMD 形式（古い形式）にはまだ対応していません（後日対応予定）。PMX Editor などで PMX に変換すると読み込めます");
        }
        if (!signature.SequenceEqual("PMX "u8))
        {
            throw new ModelFormatException("PMX ファイルではありません（ファイルの先頭の印が「PMX 」ではありません）");
        }
        float version = c.ReadSingle();
        if (!(version >= 2.0f - VersionTolerance))
        {
            throw new ModelFormatException($"PMX の版 {version} は読めません（2.0 と 2.1 に対応しています）");
        }
        if (version > 2.1f + VersionTolerance)
        {
            report.Warning(LoadIssueCategory.Unsupported, $"PMX {version:0.0} は知らない版のため、2.1 として読みました");
        }

        int globalCount = c.ReadByte();
        if (globalCount < 8)
        {
            throw new ModelFormatException($"ヘッダーの設定の数（{globalCount}）が足りません（8 個必要）");
        }
        byte[] globals = c.Take(globalCount).ToArray();
        if (globalCount > 8)
        {
            report.Info(LoadIssueCategory.Note, $"ヘッダーに知らない設定が {globalCount - 8} 個あったため、無視しました");
        }
        PmxTextEncoding textEncoding = globals[0] switch
        {
            0 => PmxTextEncoding.Utf16,
            1 => PmxTextEncoding.Utf8,
            _ => throw new ModelFormatException($"文字コードの指定（{globals[0]}）が正しくありません（0 = UTF-16、1 = UTF-8）"),
        };
        Encoding encoding = textEncoding == PmxTextEncoding.Utf16 ? Encoding.Unicode : Encoding.UTF8;
        int additionalUvCount = globals[1];
        if (additionalUvCount > 4)
        {
            throw new ModelFormatException($"追加 UV の数（{additionalUvCount}）が正しくありません（0〜4）");
        }
        var sizes = new PmxIndexSizes(
            IndexSize(globals[2], "頂点"),
            IndexSize(globals[3], "テクスチャ"),
            IndexSize(globals[4], "材質"),
            IndexSize(globals[5], "ボーン"),
            IndexSize(globals[6], "モーフ"),
            IndexSize(globals[7], "剛体"));

        string Text() => c.ReadText(encoding).TrimEnd('\0');
        int VertexIndex() => sizes.Vertex switch { 1 => c.ReadByte(), 2 => c.ReadUInt16(), _ => c.ReadInt32() };
        int Index(byte size) => size switch { 1 => c.ReadSByte(), 2 => c.ReadInt16(), _ => c.ReadInt32() };

        c.Section = "モデル名";
        string name = Text();
        string nameUniversal = Text();
        c.Section = "コメント";
        string comment = Text();
        string commentUniversal = Text();

        // 頂点
        c.Section = "頂点";
        int vertexCount = c.ReadCount("頂点", 37 + sizes.Bone);
        var vertices = new PmxVertex[vertexCount];
        var additionalUvs = new System.Numerics.Vector4[vertexCount * additionalUvCount];
        for (int i = 0; i < vertexCount; i++)
        {
            ref PmxVertex v = ref vertices[i];
            v.Position = c.ReadVector3();
            v.Normal = c.ReadVector3();
            v.Uv = c.ReadVector2();
            for (int j = 0; j < additionalUvCount; j++)
            {
                additionalUvs[(i * additionalUvCount) + j] = c.ReadVector4();
            }
            v.Bone1 = v.Bone2 = v.Bone3 = v.Bone4 = -1;
            byte type = c.ReadByte();
            switch ((PmxWeightType)type)
            {
                case PmxWeightType.Bdef1:
                    v.Bone1 = Index(sizes.Bone);
                    v.Weights = new(1f, 0f, 0f, 0f);
                    break;
                case PmxWeightType.Bdef2:
                    v.Bone1 = Index(sizes.Bone);
                    v.Bone2 = Index(sizes.Bone);
                    float w2 = c.ReadSingle();
                    v.Weights = new(w2, 1f - w2, 0f, 0f);
                    break;
                case PmxWeightType.Bdef4:
                case PmxWeightType.Qdef:
                    v.Bone1 = Index(sizes.Bone);
                    v.Bone2 = Index(sizes.Bone);
                    v.Bone3 = Index(sizes.Bone);
                    v.Bone4 = Index(sizes.Bone);
                    v.Weights = c.ReadVector4();
                    break;
                case PmxWeightType.Sdef:
                    v.Bone1 = Index(sizes.Bone);
                    v.Bone2 = Index(sizes.Bone);
                    float ws = c.ReadSingle();
                    v.Weights = new(ws, 1f - ws, 0f, 0f);
                    v.SdefC = c.ReadVector3();
                    v.SdefR0 = c.ReadVector3();
                    v.SdefR1 = c.ReadVector3();
                    break;
                default:
                    throw new ModelFormatException($"頂点 {i} 番の変形方式（{type}）が正しくありません（位置 {c.Position - 1:N0} バイト目）");
            }
            v.WeightType = (PmxWeightType)type;
            v.EdgeScale = c.ReadSingle();
        }

        // 面
        c.Section = "面";
        int indexCount = c.ReadCount("面の頂点番号", sizes.Vertex);
        var indices = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            indices[i] = VertexIndex();
        }

        // テクスチャ
        c.Section = "テクスチャ";
        int textureCount = c.ReadCount("テクスチャ", 4);
        var textures = new string[textureCount];
        for (int i = 0; i < textureCount; i++)
        {
            textures[i] = Text();
        }

        // 材質
        c.Section = "材質";
        int materialCount = c.ReadCount("材質", 80);
        var materials = new PmxMaterial[materialCount];
        for (int i = 0; i < materialCount; i++)
        {
            c.Section = $"材質 {i} 番";
            string mName = Text();
            string mNameU = Text();
            var diffuse = c.ReadVector4();
            var specular = c.ReadVector3();
            float specularity = c.ReadSingle();
            var ambient = c.ReadVector3();
            var flags = (PmxMaterialFlags)c.ReadByte();
            var edgeColor = c.ReadVector4();
            float edgeSize = c.ReadSingle();
            int texture = Index(sizes.Texture);
            int sphere = Index(sizes.Texture);
            var sphereMode = (PmxSphereMode)c.ReadByte();
            byte toonKind = c.ReadByte();
            bool sharedToon;
            int toon;
            switch (toonKind)
            {
                case 0:
                    sharedToon = false;
                    toon = Index(sizes.Texture);
                    break;
                case 1:
                    sharedToon = true;
                    toon = c.ReadByte();
                    break;
                default:
                    throw new ModelFormatException($"材質「{mName}」のトゥーンの指定（{toonKind}）が正しくありません");
            }
            string memo = Text();
            int materialIndexCount = c.ReadInt32();
            materials[i] = new PmxMaterial
            {
                Name = mName,
                NameUniversal = mNameU,
                Diffuse = diffuse,
                Specular = specular,
                Specularity = specularity,
                Ambient = ambient,
                Flags = flags,
                EdgeColor = edgeColor,
                EdgeSize = edgeSize,
                TextureIndex = texture,
                SphereTextureIndex = sphere,
                SphereMode = sphereMode,
                SharedToon = sharedToon,
                ToonIndex = toon,
                Memo = memo,
                IndexCount = materialIndexCount,
            };
        }

        // ボーン
        c.Section = "ボーン";
        int boneCount = c.ReadCount("ボーン", 28);
        var bones = new PmxBone[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            c.Section = $"ボーン {i} 番";
            string bName = Text();
            string bNameU = Text();
            var position = c.ReadVector3();
            int parent = Index(sizes.Bone);
            int layer = c.ReadInt32();
            var flags = (PmxBoneFlags)c.ReadUInt16();

            int tailBone = -1;
            System.Numerics.Vector3 tailOffset = default;
            if ((flags & PmxBoneFlags.TailIsBone) != 0)
            {
                tailBone = Index(sizes.Bone);
            }
            else
            {
                tailOffset = c.ReadVector3();
            }

            int inheritParent = -1;
            float inheritWeight = 0f;
            if ((flags & (PmxBoneFlags.InheritRotation | PmxBoneFlags.InheritTranslation)) != 0)
            {
                inheritParent = Index(sizes.Bone);
                inheritWeight = c.ReadSingle();
            }
            var fixedAxis = (flags & PmxBoneFlags.FixedAxis) != 0 ? c.ReadVector3() : default;
            System.Numerics.Vector3 localX = default, localZ = default;
            if ((flags & PmxBoneFlags.LocalAxis) != 0)
            {
                localX = c.ReadVector3();
                localZ = c.ReadVector3();
            }
            int externalKey = (flags & PmxBoneFlags.ExternalParent) != 0 ? c.ReadInt32() : 0;

            int ikTarget = -1;
            int ikLoop = 0;
            float ikLimit = 0f;
            PmxIkLink[] links = [];
            if ((flags & PmxBoneFlags.IK) != 0)
            {
                ikTarget = Index(sizes.Bone);
                ikLoop = c.ReadInt32();
                ikLimit = c.ReadSingle();
                int linkCount = c.ReadCount($"ボーン「{bName}」の IK リンク", sizes.Bone + 1);
                links = new PmxIkLink[linkCount];
                for (int k = 0; k < linkCount; k++)
                {
                    int linkBone = Index(sizes.Bone);
                    bool hasLimits = c.ReadByte() != 0;
                    links[k] = hasLimits
                        ? new PmxIkLink { Bone = linkBone, HasLimits = true, MinAngle = c.ReadVector3(), MaxAngle = c.ReadVector3() }
                        : new PmxIkLink { Bone = linkBone };
                }
            }

            bones[i] = new PmxBone
            {
                Name = bName,
                NameUniversal = bNameU,
                Position = position,
                ParentIndex = parent,
                Layer = layer,
                Flags = flags,
                TailBone = tailBone,
                TailOffset = tailOffset,
                InheritParent = inheritParent,
                InheritWeight = inheritWeight,
                FixedAxis = fixedAxis,
                LocalAxisX = localX,
                LocalAxisZ = localZ,
                ExternalParentKey = externalKey,
                IkTarget = ikTarget,
                IkLoopCount = ikLoop,
                IkLimitAngle = ikLimit,
                IkLinks = links,
            };
        }

        // モーフ
        c.Section = "モーフ";
        int morphCount = c.ReadCount("モーフ", 14);
        var morphs = new PmxMorph[morphCount];
        for (int i = 0; i < morphCount; i++)
        {
            c.Section = $"モーフ {i} 番";
            morphs[i] = ReadMorph(c, Text, VertexIndex, Index, sizes);
        }

        // 表示枠（古い書き出し器ではここから後ろが無いことがあるので、無ければ 0 個として扱う）
        var displayFrames = Array.Empty<PmxDisplayFrame>();
        if (!SectionMissing(c, report, "表示枠"))
        {
            c.Section = "表示枠";
            int frameCount = c.ReadCount("表示枠", 13);
            displayFrames = new PmxDisplayFrame[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                c.Section = $"表示枠 {i} 番";
                string fName = Text();
                string fNameU = Text();
                bool special = c.ReadByte() != 0;
                int elementCount = c.ReadCount($"表示枠「{fName}」の要素", 2);
                var elements = new PmxDisplayElement[elementCount];
                for (int k = 0; k < elementCount; k++)
                {
                    byte target = c.ReadByte();
                    elements[k] = target switch
                    {
                        0 => new PmxDisplayElement(PmxDisplayTarget.Bone, Index(sizes.Bone)),
                        1 => new PmxDisplayElement(PmxDisplayTarget.Morph, Index(sizes.Morph)),
                        _ => throw new ModelFormatException($"表示枠「{fName}」の要素の種類（{target}）が正しくありません"),
                    };
                }
                displayFrames[i] = new PmxDisplayFrame { Name = fName, NameUniversal = fNameU, IsSpecial = special, Elements = elements };
            }
        }

        // 剛体
        var rigidBodies = Array.Empty<PmxRigidBody>();
        if (!SectionMissing(c, report, "剛体"))
        {
            c.Section = "剛体";
            int rigidCount = c.ReadCount("剛体", 70);
            rigidBodies = new PmxRigidBody[rigidCount];
            for (int i = 0; i < rigidCount; i++)
            {
                c.Section = $"剛体 {i} 番";
                rigidBodies[i] = new PmxRigidBody
                {
                    Name = Text(),
                    NameUniversal = Text(),
                    BoneIndex = Index(sizes.Bone),
                    Group = c.ReadByte(),
                    NoCollisionMask = c.ReadUInt16(),
                    Shape = (PmxRigidShape)c.ReadByte(),
                    Size = c.ReadVector3(),
                    Position = c.ReadVector3(),
                    Rotation = c.ReadVector3(),
                    Mass = c.ReadSingle(),
                    LinearDamping = c.ReadSingle(),
                    AngularDamping = c.ReadSingle(),
                    Restitution = c.ReadSingle(),
                    Friction = c.ReadSingle(),
                    Mode = (PmxPhysicsMode)c.ReadByte(),
                };
            }
        }

        // ジョイント
        var joints = Array.Empty<PmxJoint>();
        if (!SectionMissing(c, report, "ジョイント"))
        {
            c.Section = "ジョイント";
            int jointCount = c.ReadCount("ジョイント", 105);
            joints = new PmxJoint[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                c.Section = $"ジョイント {i} 番";
                joints[i] = new PmxJoint
                {
                    Name = Text(),
                    NameUniversal = Text(),
                    Type = (PmxJointType)c.ReadByte(),
                    RigidBodyA = Index(sizes.RigidBody),
                    RigidBodyB = Index(sizes.RigidBody),
                    Position = c.ReadVector3(),
                    Rotation = c.ReadVector3(),
                    PositionMin = c.ReadVector3(),
                    PositionMax = c.ReadVector3(),
                    RotationMin = c.ReadVector3(),
                    RotationMax = c.ReadVector3(),
                    PositionSpring = c.ReadVector3(),
                    RotationSpring = c.ReadVector3(),
                };
            }
        }

        // ソフトボディ（PMX 2.1）。中身はまだ読まない
        int softBodyCount = 0;
        if (version >= 2.1f - VersionTolerance && c.Remaining >= 4)
        {
            c.Section = "ソフトボディ";
            softBodyCount = Math.Max(0, c.ReadInt32());
        }
        else if (c.Remaining > 0)
        {
            report.Info(LoadIssueCategory.Note, $"ファイルの末尾に、読まなかったデータが {c.Remaining:N0} バイトありました");
        }

        return new PmxDocument
        {
            Version = version,
            Encoding = textEncoding,
            AdditionalUvCount = additionalUvCount,
            IndexSizes = sizes,
            Name = name,
            NameUniversal = nameUniversal,
            Comment = comment,
            CommentUniversal = commentUniversal,
            Vertices = vertices,
            AdditionalUvs = additionalUvs,
            Indices = indices,
            Textures = textures,
            Materials = materials,
            Bones = bones,
            Morphs = morphs,
            DisplayFrames = displayFrames,
            RigidBodies = rigidBodies,
            Joints = joints,
            SoftBodyCount = softBodyCount,
        };
    }

    private static PmxMorph ReadMorph(BinaryCursor c, Func<string> text, Func<int> vertexIndex, Func<byte, int> index, PmxIndexSizes sizes)
    {
        string name = text();
        string nameUniversal = text();
        var panel = (PmxMorphPanel)c.ReadByte();
        byte rawType = c.ReadByte();
        var type = (PmxMorphType)rawType;
        int minimum = type switch
        {
            PmxMorphType.Group or PmxMorphType.Flip => sizes.Morph + 4,
            PmxMorphType.Vertex => sizes.Vertex + 12,
            PmxMorphType.Bone => sizes.Bone + 28,
            PmxMorphType.Material => sizes.Material + 113,
            PmxMorphType.Impulse => sizes.RigidBody + 25,
            >= PmxMorphType.Uv and <= PmxMorphType.AdditionalUv4 => sizes.Vertex + 16,
            _ => throw new ModelFormatException($"モーフ「{name}」の種類（{rawType}）が正しくありません"),
        };
        int count = c.ReadCount($"モーフ「{name}」の要素", minimum);

        switch (type)
        {
            case PmxMorphType.Group or PmxMorphType.Flip:
                var groups = new PmxGroupOffset[count];
                for (int k = 0; k < count; k++)
                {
                    groups[k] = new PmxGroupOffset(index(sizes.Morph), c.ReadSingle());
                }
                return new PmxMorph { Name = name, NameUniversal = nameUniversal, Panel = panel, Type = type, GroupOffsets = groups };
            case PmxMorphType.Vertex:
                var vertexOffsets = new PmxVertexOffset[count];
                for (int k = 0; k < count; k++)
                {
                    vertexOffsets[k] = new PmxVertexOffset(vertexIndex(), c.ReadVector3());
                }
                return new PmxMorph { Name = name, NameUniversal = nameUniversal, Panel = panel, Type = type, VertexOffsets = vertexOffsets };
            case PmxMorphType.Bone:
                var boneOffsets = new PmxBoneOffset[count];
                for (int k = 0; k < count; k++)
                {
                    int bone = index(sizes.Bone);
                    var translation = c.ReadVector3();
                    var q = c.ReadVector4();
                    boneOffsets[k] = new PmxBoneOffset(bone, translation, new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W));
                }
                return new PmxMorph { Name = name, NameUniversal = nameUniversal, Panel = panel, Type = type, BoneOffsets = boneOffsets };
            case PmxMorphType.Material:
                var materialOffsets = new PmxMaterialOffset[count];
                for (int k = 0; k < count; k++)
                {
                    materialOffsets[k] = new PmxMaterialOffset(
                        index(sizes.Material),
                        (PmxMaterialOperation)c.ReadByte(),
                        c.ReadVector4(),
                        c.ReadVector3(),
                        c.ReadSingle(),
                        c.ReadVector3(),
                        c.ReadVector4(),
                        c.ReadSingle(),
                        c.ReadVector4(),
                        c.ReadVector4(),
                        c.ReadVector4());
                }
                return new PmxMorph { Name = name, NameUniversal = nameUniversal, Panel = panel, Type = type, MaterialOffsets = materialOffsets };
            case PmxMorphType.Impulse:
                var impulses = new PmxImpulseOffset[count];
                for (int k = 0; k < count; k++)
                {
                    impulses[k] = new PmxImpulseOffset(index(sizes.RigidBody), c.ReadByte() != 0, c.ReadVector3(), c.ReadVector3());
                }
                return new PmxMorph { Name = name, NameUniversal = nameUniversal, Panel = panel, Type = type, ImpulseOffsets = impulses };
            default:
                var uvOffsets = new PmxUvOffset[count];
                for (int k = 0; k < count; k++)
                {
                    uvOffsets[k] = new PmxUvOffset(vertexIndex(), c.ReadVector4());
                }
                return new PmxMorph { Name = name, NameUniversal = nameUniversal, Panel = panel, Type = type, UvOffsets = uvOffsets };
        }
    }

    private static bool SectionMissing(BinaryCursor c, LoadReport report, string section)
    {
        if (c.Remaining > 0)
        {
            return false;
        }
        report.Info(LoadIssueCategory.Note, $"「{section}」の項目がファイルにありませんでした（0 個として扱います）");
        return true;
    }

    private static byte IndexSize(byte size, string what) => size is 1 or 2 or 4
        ? size
        : throw new ModelFormatException($"{what}の番号の大きさ（{size}）が正しくありません（1・2・4 のどれか）");
}
